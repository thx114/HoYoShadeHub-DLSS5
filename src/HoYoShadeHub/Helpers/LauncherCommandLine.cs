using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;

namespace HoYoShadeHub.Helpers;

internal static class LauncherCommandLine
{
    internal const string WaitForParentFlag = "--wait-for-parent";

    internal static bool IsCliVerb(string arg) => arg.ToLowerInvariant() is
        "run" or "auto" or "action" or "stopgame" or "killgame" or "startgame"
        or "testgame" or "test-game" or "selftest" or "collectlogs" or "collect-logs" or "sessionlogs";

    internal static string BuildArguments(IEnumerable<string> args) => string.Join(" ", args.Select(QuoteArgument));

    internal static string QuoteArgument(string value)
    {
        if (value.Length > 0 && !value.Any(c => c is ' ' or '\t' or '"'))
        {
            return value;
        }

        var result = new StringBuilder(value.Length + 2).Append('"');
        int backslashes = 0;
        foreach (char c in value)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            result.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            result.Append(c);
            backslashes = 0;
        }

        return result.Append('\\', backslashes * 2).Append('"').ToString();
    }

    internal static string BuildRestartArguments()
    {
        using var current = Process.GetCurrentProcess();
        return BuildArguments(new[]
        {
            WaitForParentFlag,
            current.Id.ToString(CultureInfo.InvariantCulture),
            current.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture),
        });
    }

    // Runs only in the newly started process, before it acquires the single-instance mutex.
    internal static string[] WaitForParentExit(string[] args)
    {
        int index = Array.IndexOf(args, WaitForParentFlag);
        if (index < 0)
        {
            return args;
        }

        if (index + 2 >= args.Length
            || !int.TryParse(args[index + 1], NumberStyles.None, CultureInfo.InvariantCulture, out int pid)
            || pid <= 0 || pid == Environment.ProcessId
            || !long.TryParse(args[index + 2], NumberStyles.None, CultureInfo.InvariantCulture, out long startTicks)
            || startTicks <= 0
            || Array.LastIndexOf(args, WaitForParentFlag) != index)
        {
            throw new ArgumentException("Invalid restart parent identity.");
        }

        var forwarded = args.Take(index).Concat(args.Skip(index + 3)).ToArray();
        Process parent;
        try
        {
            parent = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return forwarded;
        }

        using (parent)
        {
            if (parent.HasExited)
            {
                return forwarded;
            }

            // A recycled PID must not cause a wait on an unrelated process.
            if (parent.StartTime.ToUniversalTime().Ticks != startTicks)
            {
                return forwarded;
            }

            if (!parent.WaitForExit(30_000))
            {
                throw new TimeoutException("The previous launcher did not exit within 30 seconds.");
            }
        }

        return forwarded;
    }
}
