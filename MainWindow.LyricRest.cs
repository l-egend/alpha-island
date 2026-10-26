using System.Windows;
using alpha_island.Animation;
using alpha_island.Core;

namespace alpha_island;

public partial class MainWindow
{
    private bool _lyricRestShowing;
    private LyricRestSpan? _lyricRestSpan;
    private TimeSpan _lyricRestPosition;
    private float _lyricBeatEnergy;

    private void ApplyLyricRest(LyricRestSpan span, TimeSpan position)
    {
        bool changed = !_lyricRestShowing || _lyricRestSpan != span;
        _lyricRestSpan = span; _lyricRestPosition = position;
        if (changed)
        {
            ResetLyricPresentation(); ResetPillLyricRoll();
            _lyricIndex = -1;
            LyricCurrentDim.Text = ""; LyricCurrentBright.Text = "";
            LyricPrev.Text = span.PreviousIndex >= 0 ? Tidy(_lyricsDoc!.Lines[span.PreviousIndex].Text) : "";
            LyricNext.Text = span.NextIndex >= 0 ? Tidy(_lyricsDoc!.Lines[span.NextIndex].Text) : "";
            LyricTranslation.Text = ""; LyricTranslation.Visibility = Visibility.Collapsed;
            _lyricWipe.Rect = Rect.Empty;
            SetLyricRestVisible(true);
            SetPillLyric("");
        }
        SyncLyricRestIndicators();
    }

    private void SetLyricRestVisible(bool visible)
    {
        if (_lyricRestShowing == visible) return;
        _lyricRestShowing = visible;
        if (!visible) _lyricRestSpan = null;
        SyncLyricRestIndicators();
    }

    private void SyncLyricRestIndicators()
    {
        if (PillBeatDots == null || CardBeatDots == null) return;
        bool pillRest = _lyricRestShowing && !UpNextActive;
        bool changed = (PillBeatDots.Visibility == Visibility.Visible) != pillRest;
        PillBeatDots.Visibility = pillRest ? Visibility.Visible : Visibility.Collapsed;
        PillLyricStage.Visibility = PillLyricGhost.Visibility = pillRest ? Visibility.Collapsed : Visibility.Visible;
        CardBeatDots.Visibility = _lyricRestShowing ? Visibility.Visible : Visibility.Collapsed;
        LyricCurrentDim.Visibility = LyricCurrentBright.Visibility = _lyricRestShowing ? Visibility.Collapsed : Visibility.Visible;
        bool animate = _lyricRestShowing && _smtc?.Current?.IsPlaying == true && !GameDoNotDisturbActive && !_exiting
            && !MotionPolicy.InstantMorph && !MotionPolicy.PowerSave;
        float strength = System.Threading.Volatile.Read(ref _lyricBeatEnergy);
        var restElapsed = TimeSpan.FromSeconds(Math.Max(0, _lyricRestPosition.TotalSeconds - (_lyricRestSpan?.StartSeconds ?? 0)));
        PillBeatDots.SetFrame(restElapsed, animate && pillRest && _state == IslandState.Pill && WantPillLayerVisible(), strength);
        CardBeatDots.SetFrame(restElapsed, animate && _state == IslandState.Card && _pages.ActiveId == IslandPageIds.Music, strength);
        if (changed) UpdatePillLyricScroll();
    }
}
