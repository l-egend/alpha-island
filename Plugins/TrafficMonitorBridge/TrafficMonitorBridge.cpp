#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <sddl.h>
#include <cstdint>
#include <cstddef>
#include <cmath>
#include <string>
#include <vector>
#include "upstream/PluginInterface.h"

namespace {
constexpr std::uint32_t Magic = 0x4149544d; // AITM, little endian
struct alignas(8) Snapshot {
    std::uint32_t magic, version;
    volatile LONG sequence;
    std::uint32_t size;
    std::int64_t utcFileTime;
    std::uint32_t processId, flags;
    std::uint64_t downBps, upBps, todayDown, todayUp, epoch;
    std::uint32_t localDate, reserved;
};
static_assert(sizeof(Snapshot) == 80 && offsetof(Snapshot, downBps) == 32 && offsetof(Snapshot, epoch) == 64);

std::wstring MapName() {
    // Regression tests use a separate namespace and never change the real producer's mapping.
    wchar_t test[256]{};
    const DWORD length = GetEnvironmentVariableW(L"ALPHA_ISLAND_TM_TEST_MAP", test, 256);
    if (length > 0 && length < 256 && std::wstring(test).rfind(L"Local\\AlphaIsland.TrafficMonitor.Test.", 0) == 0) return test;
    HANDLE token{};
    if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &token)) return {};
    DWORD bytes{}; GetTokenInformation(token, TokenUser, nullptr, 0, &bytes);
    std::vector<unsigned char> buffer(bytes);
    const bool ok = GetTokenInformation(token, TokenUser, buffer.data(), bytes, &bytes) != FALSE;
    CloseHandle(token);
    if (!ok) return {};
    LPWSTR sid{};
    if (!ConvertSidToStringSidW(reinterpret_cast<TOKEN_USER*>(buffer.data())->User.Sid, &sid)) return {};
    std::wstring result = L"Local\\AlphaIsland.TrafficMonitor.v1_"; result += sid; LocalFree(sid);
    return result;
}

std::uint64_t Bytes(double value) {
    if (!std::isfinite(value) || value <= 0) return 0;
    // Protocol counters use signed Int64 in the managed SDK.
    if (value >= 9223372036854775808.0) return 9223372036854775807ULL;
    return static_cast<std::uint64_t>(value);
}

class Bridge final : public ITMPlugin {
    ITrafficMonitor* app_{};
    HANDLE mapping_{}, writer_{};
    Snapshot* view_{};
    SRWLOCK lock_ = SRWLOCK_INIT;
    const std::uint64_t epoch_ = GetTickCount64();

    void Publish(std::uint64_t down, std::uint64_t up, bool totals, std::uint64_t todayDown, std::uint64_t todayUp) {
        if (!view_) {
            const auto name = MapName();
            if (name.empty()) return;
            writer_ = CreateMutexW(nullptr, FALSE, (name + L"_writer").c_str());
            mapping_ = CreateFileMappingW(INVALID_HANDLE_VALUE, nullptr, PAGE_READWRITE, 0, sizeof(Snapshot), name.c_str());
            if (mapping_) view_ = static_cast<Snapshot*>(MapViewOfFile(mapping_, FILE_MAP_WRITE, 0, 0, sizeof(Snapshot)));
            if (!view_) {
                if (mapping_) CloseHandle(mapping_);
                mapping_ = nullptr;
                if (writer_) CloseHandle(writer_);
                writer_ = nullptr;
                return;
            }
        }
        if (!writer_) return;
        const DWORD acquired = WaitForSingleObject(writer_, 0);
        if (acquired != WAIT_OBJECT_0 && acquired != WAIT_ABANDONED) return;
        FILETIME utc{}; GetSystemTimeAsFileTime(&utc);
        SYSTEMTIME local{}; GetLocalTime(&local);
        // Recover an odd sequence left by a terminated producer; mutex serializes multiple instances.
        const LONG sequence = ((InterlockedCompareExchange(&view_->sequence, 0, 0) & 0x7ffffffe) + 2u) & 0x7ffffffe;
        InterlockedExchange(&view_->sequence, sequence - 1);
        view_->magic = Magic; view_->version = 1; view_->size = sizeof(Snapshot);
        view_->utcFileTime = static_cast<std::int64_t>((static_cast<std::uint64_t>(utc.dwHighDateTime) << 32) | utc.dwLowDateTime);
        view_->processId = GetCurrentProcessId(); view_->flags = totals ? 1 : 0;
        view_->downBps = down; view_->upBps = up;
        view_->todayDown = todayDown; view_->todayUp = todayUp; view_->epoch = epoch_;
        view_->localDate = local.wYear * 10000u + local.wMonth * 100u + local.wDay; view_->reserved = 0;
        MemoryBarrier(); InterlockedExchange(&view_->sequence, sequence);
        ReleaseMutex(writer_);
    }
    void ReadApp() {
        // V1.86 exposes ITrafficMonitor's initial interface as version 0 (not ITMPlugin's version 7).
        if (!app_ || app_->GetAPIVersion() < 0) return;
        Publish(Bytes(app_->GetMonitorValue(ITrafficMonitor::MI_DOWN)), Bytes(app_->GetMonitorValue(ITrafficMonitor::MI_UP)), true,
            Bytes(app_->GetMonitorValue(ITrafficMonitor::MI_TODAY_DOWN_TRAFFIC)), Bytes(app_->GetMonitorValue(ITrafficMonitor::MI_TODAY_UP_TRAFFIC)));
    }
public:
    ~Bridge() {
        if (view_) UnmapViewOfFile(view_);
        if (mapping_) CloseHandle(mapping_);
        if (writer_) CloseHandle(writer_);
    }
    IPluginItem* GetItem(int) override { return nullptr; } // Data bridge, no extra TrafficMonitor display item.
    void DataRequired() override {
        AcquireSRWLockExclusive(&lock_); ReadApp(); ReleaseSRWLockExclusive(&lock_);
    }
    void OnInitialize(ITrafficMonitor* app) override {
        AcquireSRWLockExclusive(&lock_); app_ = app; ReleaseSRWLockExclusive(&lock_);
    }
    void OnMonitorInfo(const MonitorInfo& info) override {
        AcquireSRWLockExclusive(&lock_);
        if (app_) ReadApp(); else Publish(info.down_speed, info.up_speed, false, 0, 0);
        ReleaseSRWLockExclusive(&lock_);
    }
    const wchar_t* GetInfo(PluginInfoIndex index) override {
        switch (index) {
        case TMI_NAME: return L"Alpha Island TrafficMonitor Bridge";
        case TMI_DESCRIPTION: return L"Shares network rates and today's byte counters with Alpha Island for the current user.";
        case TMI_AUTHOR: return L"Alpha Island";
        case TMI_COPYRIGHT: return L"Alpha Island; TrafficMonitor interface (C) Zhong Yang";
        case TMI_VERSION: return L"1.0.0";
        case TMI_URL: return L"https://github.com/zhongyang219/TrafficMonitor/wiki/插件开发指南";
        default: return L"";
        }
    }
};
}

extern "C" __declspec(dllexport) ITMPlugin* TMPluginGetInstance() {
    static Bridge bridge; return &bridge;
}
