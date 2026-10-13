using System.Text.RegularExpressions;

namespace Lineup.Web.Components.Settings;

/// <summary>
/// Displays and controls a virtual Lineup Link connection bundle.
/// </summary>
public partial class LineupLinkBundlePanel
{
    [GeneratedRegex("(?<property>\"(?:\\\\.|[^\"\\\\])*\")(?=\\s*:)|(?<string>\"(?:\\\\.|[^\"\\\\])*\")|(?<number>-?\\d+(?:\\.\\d+)?(?:[eE][+-]?\\d+)?)|(?<boolean>\\b(?:true|false)\\b)|(?<null>\\bnull\\b)")]
    private static partial Regex JsonTokenRegex();
}
