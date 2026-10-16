namespace alpha_island.Interop;

internal sealed record MessengerActivationOperations(Func<bool> RestoreWindow, Func<bool> OpenTray, Func<bool> StartClient);
