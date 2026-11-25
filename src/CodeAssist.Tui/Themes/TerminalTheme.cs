using Spectre.Console;

namespace CodeAssist.Tui.Themes;

public static class TerminalTheme
{
    public static Color Background { get; } = new(14, 14, 17);
    public static Color Surface { get; } = new(28, 27, 34);
    public static Color Border { get; } = new(54, 53, 63);
    public static Color Text { get; } = new(232, 231, 237);
    public static Color Muted { get; } = new(164, 161, 176);
    public static Color Selection { get; } = new(191, 154, 255);
    public static Color Activity { get; } = new(121, 180, 255);
    public static Color Success { get; } = new(141, 206, 168);
    public static Color Warning { get; } = new(233, 189, 115);
    public static Color Error { get; } = new(239, 149, 158);
}