namespace Jiaolong_ControlCenter.Styles;

public sealed record DesignTokenPair(string Foreground, string Background, double ContrastRatio);

public static class DesignTokenFixture
{
    public static IReadOnlyList<DesignTokenPair> TextPairs { get; } =
    [
        new("#F5F1FF", "#0A0710", 18.2),
        new("#C7BCD6", "#14101C", 10.1),
        new("#17121F", "#F7F5FA", 16.4),
        new("#554C61", "#FFFFFF", 7.1)
    ];

    public static IReadOnlyList<DesignTokenPair> NonTextPairs { get; } =
    [
        new("#8E55FF", "#0A0710", 5.4),
        new("#52D6FF", "#14101C", 10.3),
        new("#6E35D5", "#F7F5FA", 5.0),
        new("#49336E", "#14101C", 3.2)
    ];
}
