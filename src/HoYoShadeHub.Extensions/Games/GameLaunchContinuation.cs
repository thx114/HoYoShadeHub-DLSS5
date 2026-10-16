using HoYoShadeHub.Extensions.ReShade;

namespace HoYoShadeHub.Extensions.Games;

public static class GameLaunchContinuation
{
    // Package setup may stop independently; a changed launch context still cancels launch.
    public static bool AfterPackActions(PackActionOncePolicy.ExecutionResult result, bool contextCurrent) =>
        contextCurrent && result.Reason != PackActionOncePolicy.BlockReason.ContextChanged;
}
