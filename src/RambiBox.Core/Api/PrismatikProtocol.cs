using System.Globalization;
using System.Text;
using RambiBox.Core.Config;

namespace RambiBox.Core.Api;

/// <summary>
/// Strings, parsers and value mappings of the Prismatik / Lightpack text API, ported from
/// psieg/Lightpack Software/src/ApiServer.cpp, ApiServerSetColorTask.cpp and LightpackPluginInterface.cpp
/// (Prismatik 5.11.2.31, API 2.2). Every line the server sends ends in "\r\n".
/// </summary>
public static class PrismatikProtocol
{
    public const string ApiVersion = "2.2";
    public const string Eol = "\r\n";

    /// <summary>Sent right after a client connects (identical to Prismatik; clients parse "API v…" from it).</summary>
    public const string Greeting = "Lightpack API v1.4 - Prismatik API v" + ApiVersion + " (type \"help\" for more info)\r\n";

    public const string Unknown = "unknown command\r\n";
    public const string Deprecated = "deprecated command, not supported\r\n";
    public const string Goodbye = "Goodbye!\r\n";
    public const string AuthRequired = "authorization required\r\n";
    public const string ApiKeyOk = "ok\r\n";
    public const string ApiKeyFail = "fail\r\n";

    public const string StatusOn = "status:on\r\n";
    public const string StatusOff = "status:off\r\n";
    public const string StatusDeviceError = "status:device error\r\n";
    public const string StatusUnknown = "status:unknown\r\n";
    public const string StatusApiBusy = "statusapi:busy\r\n";
    public const string StatusApiIdle = "statusapi:idle\r\n";
    public const string ModeAmbilight = "mode:ambilight\r\n";
    public const string ModeMoodlamp = "mode:moodlamp\r\n";
    public const string PersistOn = "persistonunlock:on\r\n";
    public const string PersistOff = "persistonunlock:off\r\n";

    public const string LockSuccess = "lock:success\r\n";
    public const string LockBusy = "lock:busy\r\n";
    public const string UnlockSuccess = "unlock:success\r\n";
    public const string UnlockNotLocked = "unlock:not locked\r\n";

    // Set-commands answer only these.
    public const string Ok = "ok\r\n";
    public const string Error = "error\r\n";
    public const string Busy = "busy\r\n";
    public const string NotLocked = "not locked\r\n";

    /// <summary>RambiBox has a single settings set; it is exposed as one profile.</summary>
    public const string ProfileName = "Default";

    // Prismatik's accepted ranges (SettingsDefaults.hpp, Profile::Device).
    public const double GammaMin = 0.01, GammaMax = 10.0;
    public const int BrightnessMin = 0, BrightnessMax = 100;
    public const int SmoothMin = 0, SmoothMax = 255;

    /// <summary>
    /// Prismatik's "smooth slowdown" (0..255, steps of the Lightpack firmware fade) has no time unit; RambiBox
    /// maps one step to 4 ms of its smoothing time constant: 0 = off, 25 ≈ RambiBox's default 100 ms,
    /// 250..255 = 1000 ms (the UI maximum).
    /// </summary>
    public const int SmoothMsPerStep = 4;
    public const int MaxSmoothingMs = 1000;

    public static int SmoothToMs(int smooth) => Math.Min(Math.Clamp(smooth, SmoothMin, SmoothMax) * SmoothMsPerStep, MaxSmoothingMs);
    public static int MsToSmooth(int ms) => Math.Clamp((int)Math.Round(ms / (double)SmoothMsPerStep, MidpointRounding.AwayFromZero), SmoothMin, SmoothMax);
    public static int BrightnessToApi(double brightness01) => Math.Clamp((int)Math.Round(brightness01 * 100, MidpointRounding.AwayFromZero), 0, 100);

    /// <summary>Formats a double like Qt's QString::arg(double) ('g', 6 significant digits): 1 → "1", 33.619 → "33.619".</summary>
    public static string FormatDouble(double v)
    {
        if (!double.IsFinite(v)) v = 0;
        return v.ToString("G6", CultureInfo.InvariantCulture).Replace("E", "e");
    }

    private static int Digit(char c) => c is >= '0' and <= '9' ? c - '0' : -1;

    /// <summary>One parsed <c>setcolor</c> entry (0-based LED index).</summary>
    public readonly record struct LedColor(int Index, byte R, byte G, byte B);

    /// <summary>
    /// Parses the payload of <c>setcolor:</c> — "N-R,G,B;N-R,G,B;…" with 1-based LED numbers (Prismatik
    /// ApiServerSetColorTask): the trailing ';' is optional, values must be 0..255 (Prismatik rejects, doesn't clamp),
    /// no spaces, LED number 1..<paramref name="ledCount"/>. Any malformed entry fails the whole command (nothing is
    /// applied). An empty payload succeeds with no entries, like Prismatik. Differences: up to 4-digit LED numbers
    /// (Prismatik: 3) and exactly three components are required (Prismatik silently fills missing ones with 0).
    /// </summary>
    public static bool TryParseSetColor(string payload, int ledCount, List<LedColor> result)
    {
        result.Clear();
        string s = payload;
        int pos = 0;
        Span<int> rgb = stackalloc int[3];
        while (pos < s.Length)
        {
            if (s.Length - pos < 7) return false; // shortest entry is "2-0,0,0"

            int d0 = Digit(s[pos]);
            if (d0 <= 0) return false; // no leading zero, must be a digit
            int num = d0, k = pos + 1;
            while (k < s.Length && k - pos < 4 && Digit(s[k]) >= 0) num = num * 10 + Digit(s[k++]);
            if (k >= s.Length || s[k] != '-') return false;
            pos = k + 1;
            if (num < 1 || num > ledCount) return false;

            rgb.Clear();
            int idx = 0;
            bool digitExpected = true;
            for (; pos < s.Length; pos++)
            {
                char c = s[pos];
                int d = Digit(c);
                if (d < 0)
                {
                    if (c == ';') break;
                    if (c != ',' || digitExpected) return false;
                    digitExpected = true;
                    if (++idx > 2) return false;
                }
                else
                {
                    rgb[idx] = rgb[idx] * 10 + d;
                    digitExpected = false;
                    if (rgb[idx] > 255) return false;
                }
            }
            if (idx != 2 || digitExpected) return false;
            if (pos < s.Length && s[pos] == ';') pos++;
            result.Add(new LedColor(num - 1, (byte)rgb[0], (byte)rgb[1], (byte)rgb[2]));
        }
        return true;
    }

    /// <summary>
    /// Parses the payload of <c>setleds:</c> — "N-X,Y,W,H;…" (1-based LED, desktop pixels). Like Prismatik, invalid
    /// entries (bad number, LED out of range, empty size) are skipped instead of failing the command.
    /// </summary>
    public static List<(int Index, PixelRect Rect)> ParseSetLeds(string payload, int ledCount)
    {
        var list = new List<(int, PixelRect)>();
        foreach (var entry in payload.Split(';'))
        {
            if (entry.Length == 0) continue;
            int dash = entry.IndexOf('-');
            if (dash <= 0) continue;
            if (!int.TryParse(entry.AsSpan(0, dash), NumberStyles.None, CultureInfo.InvariantCulture, out int num)) continue;
            var parts = entry[(dash + 1)..].Split(',');
            if (parts.Length != 4) continue;
            var v = new int[4];
            bool ok = true;
            for (int i = 0; i < 4 && ok; i++)
                ok = int.TryParse(parts[i], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out v[i]);
            if (!ok || num < 1 || num > ledCount || v[2] <= 0 || v[3] <= 0) continue;
            list.Add((num - 1, new PixelRect(v[0], v[1], v[2], v[3])));
        }
        return list;
    }

    /// <summary>A zone (normalized to the captured monitor) in desktop pixels, as <c>getleds</c> reports it.</summary>
    public static PixelRect ZoneToPixels(LedZone z, PixelRect screen)
    {
        int l = (int)Math.Round(z.Left * screen.Width), t = (int)Math.Round(z.Top * screen.Height);
        int r = (int)Math.Round(z.Right * screen.Width), b = (int)Math.Round(z.Bottom * screen.Height);
        return new PixelRect(screen.X + l, screen.Y + t, r - l, b - t);
    }

    /// <summary>Inverse of <see cref="ZoneToPixels"/>, clamped to the monitor.</summary>
    public static ZoneUpdate PixelsToZone(int index, PixelRect rect, PixelRect screen)
    {
        float w = Math.Max(1, screen.Width), h = Math.Max(1, screen.Height);
        float l = Math.Clamp((rect.X - screen.X) / w, 0, 1), t = Math.Clamp((rect.Y - screen.Y) / h, 0, 1);
        float r = Math.Clamp((rect.X + rect.Width - screen.X) / w, 0, 1), b = Math.Clamp((rect.Y + rect.Height - screen.Y) / h, 0, 1);
        return new ZoneUpdate(index, l, t, r, b);
    }

    // ------------------------------------------------------------------ help (same layout as Prismatik)

    private static string FormatHelp(string cmd) => $"\t\t \"{cmd.Trim()}\" \r\n";

    private static string FormatHelp(string cmd, string description) =>
        new string('.', 80) + "\r\n" + cmd + " \r\n" + "\t " + description + " \r\n";

    private static string FormatHelp(string cmd, string description, string results) =>
        FormatHelp(cmd, description) + "\t Results: \r\n" + results;

    private static string FormatHelp(string cmd, string description, string examples, string results) =>
        FormatHelp(cmd, description) + "\t Examples: \r\n" + examples + "\t Results: \r\n" + results;

    /// <summary>Answer of <c>help</c>.</summary>
    public static string HelpMessage(string appVersion)
    {
        var sb = new StringBuilder();
        sb.Append("\r\n");
        sb.Append($"RambiBox {appVersion}, Prismatik API {ApiVersion}\r\n");
        sb.Append("Prismatik API is a fork of the original API\r\n");
        sb.Append("It is backwards compatible to Lightpack API 1.4\r\n");
        sb.Append("\r\n");

        sb.Append(FormatHelp("apikey:", "Command for enter an authorization key (see key in GUI)",
            FormatHelp("apikey:{1ccf5dca-119d-45a0-a683-7d90a00c418f}") + FormatHelp("apikey:IDDQD"),
            FormatHelp(ApiKeyOk) + FormatHelp(ApiKeyFail)));
        sb.Append(FormatHelp("lock", "Opens access to set-commands, If success - suspends capture and blocking access for other clients to set-commands.",
            FormatHelp(LockSuccess) + FormatHelp(LockBusy)));
        sb.Append(FormatHelp("unlock", "Closes access to set-commands. Restores device settings from the current profile, and continues the normal execution of the application.",
            FormatHelp(UnlockSuccess) + FormatHelp(UnlockNotLocked)));

        sb.Append(FormatHelp("getstatus", "Get status of the backlight",
            FormatHelp(StatusOn) + FormatHelp(StatusOff) + FormatHelp(StatusDeviceError) + FormatHelp(StatusUnknown)));
        sb.Append(FormatHelp("getstatusapi", "Get status of the lightpack API", FormatHelp(StatusApiBusy) + FormatHelp(StatusApiIdle)));
        sb.Append(FormatHelp("getprofile", "Get the name of the current profile", FormatHelp("profile:" + ProfileName)));
        sb.Append(FormatHelp("getprofiles", "Get names of the all available profiles", FormatHelp("profiles:" + ProfileName + ";")));
        sb.Append(FormatHelp("getcountleds", "Get count leds of the current profile", FormatHelp("countleds:10")));
        sb.Append(FormatHelp("getleds", "Get curent rect areas. Format: \"N-X,Y,W,H;\", where N - number of area, X,Y - position, H,W-size.",
            FormatHelp("leds:0-0,0,100,100;1-0,200,100,100;")));
        sb.Append(FormatHelp("getcolors", "Get curent color leds. Format: \"N-R,G,B;\", where N - number of led, R, G, B - red, green and blue color components.",
            FormatHelp("colors:0-0,120,200;1-0,234,23;")));
        sb.Append(FormatHelp("getfps", "Get FPS grabing", FormatHelp("fps:25.57")));
        sb.Append(FormatHelp("getscreensize", "Get size screen", FormatHelp("screensize:0,0,1024,768")));
        sb.Append(FormatHelp("getmode", "Get mode of the current profile", FormatHelp(ModeAmbilight) + FormatHelp(ModeMoodlamp)));
        sb.Append(FormatHelp("getgamma", "Get the current gamma correction value", FormatHelp("gamma:1.2")));
        sb.Append(FormatHelp("getbrightness", "Get the current brightness value", FormatHelp("brightness:100")));
        sb.Append(FormatHelp("getsmooth", "Get the current smooth value", FormatHelp("smooth:25")));
        sb.Append(FormatHelp("getpersistonunlock", "Get wether or not the last set colors should persist when unlocking", FormatHelp(PersistOn)));

        string setResults = FormatHelp(Ok) + FormatHelp(Error) + FormatHelp(Busy) + FormatHelp(NotLocked);
        sb.Append(FormatHelp("setcolor:", "Set colors on several LEDs. Format: \"N-R,G,B;\", where N - number of led, R, G, B - red, green and blue color components. Works only on locking time (see lock).",
            FormatHelp("setcolor:1-255,255,30;") + FormatHelp("setcolor:1-255,255,30;2-12,12,12;3-1,2,3;"), setResults));
        sb.Append(FormatHelp("setleds:", "Set areas on several LEDs. Format: \"N-X,Y,W,H;\", where N - number of led, X,Y - position, H,W-size. Works only on locking time (see lock).",
            FormatHelp("setleds:1-0,0,100,100;") + FormatHelp("setleds:1-0,0,100,100;2-0,100,100,100;3-100,0,100,100;"), setResults));
        sb.Append(FormatHelp("setgamma:", $"Set device gamma correction value [{FormatDouble(GammaMin)} - {FormatDouble(GammaMax)}]. Works only on locking time (see lock).",
            FormatHelp("setgamma:2.5"), setResults));
        sb.Append(FormatHelp("setbrightness:", $"Set device brightness value [{BrightnessMin} - {BrightnessMax}]. Works only on locking time (see lock).",
            FormatHelp("setbrightness:0") + FormatHelp("setbrightness:93"), setResults));
        sb.Append(FormatHelp("setsmooth:", $"Set device smooth value [{SmoothMin} - {SmoothMax}]. Works only on locking time (see lock).",
            FormatHelp("setsmooth:10") + FormatHelp("setsmooth:128"), setResults));
        sb.Append(FormatHelp("setprofile:", "Set current profile. Works only on locking time (see lock).",
            FormatHelp("setprofile:" + ProfileName), setResults));
        sb.Append(FormatHelp("newprofile:", "Create new profile. Works only on locking time (see lock).",
            FormatHelp("newprofile:16x9") + setResults));
        sb.Append(FormatHelp("deleteprofile:", "Delete profile. Works only on locking time (see lock).",
            FormatHelp("deleteprofile:16x9") + setResults));
        sb.Append(FormatHelp("setstatus:", "Set backlight status. Works only on locking time (see lock).",
            FormatHelp("setstatus:on") + FormatHelp("setstatus:off"), setResults));
        sb.Append(FormatHelp("setmode:", "Set backlight mode. Works only on locking time (see lock).",
            FormatHelp("setmode:ambilight") + FormatHelp("setmode:moodlamp"), setResults));
        sb.Append(FormatHelp("setpersistonunlock:", "Set wether or not the last set colors should persist when unlocking. Since API 2.2",
            FormatHelp("setpersistonunlock:on"), setResults));

        sb.Append(FormatHelp("?", "Short version of this help"));
        sb.Append(FormatHelp("exit", "Closes connection"));
        sb.Append("\r\n");
        return sb.ToString();
    }

    /// <summary>Answer of <c>?</c>: the command list wrapped like Prismatik's (tab-indented, below 80 columns).</summary>
    public static string ShortHelpMessage()
    {
        string[] cmds =
        [
            "apikey:", "lock", "unlock", "getstatus", "getstatusapi", "getprofile", "getprofiles",
            "getcountleds", "getleds", "getcolors", "getfps", "getscreensize", "getmode",
            "getgamma", "getbrightness", "getsmooth", "getpersistonunlock",
            "setcolor:", "setleds:", "setgamma:", "setbrightness:", "setsmooth:",
            "setprofile:", "newprofile:", "deleteprofile:", "setstatus:", "setmode:", "setpersistonunlock:",
            "exit", "help", "?",
        ];
        var sb = new StringBuilder("\r\nList of available commands:\r\n");
        string line = "\t";
        for (int i = 0; i < cmds.Length; i++)
        {
            if (line.Length + cmds[i].Length < 80) line += cmds[i].Replace(":", "");
            else
            {
                sb.Append(line).Append("\r\n");
                line = "\t" + cmds[i].Replace(":", "");
            }
            if (i != cmds.Length - 1) line += ", ";
        }
        sb.Append(line).Append("\r\n");
        sb.Append("\r\nDetailed version is available by \"help\" command. \r\n\r\n");
        return sb.ToString();
    }
}
