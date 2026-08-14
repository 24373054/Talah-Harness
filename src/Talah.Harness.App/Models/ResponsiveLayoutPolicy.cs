namespace Talah.Harness.App.Models;

public enum ResponsiveLayoutBand
{
    Narrow,
    Medium,
    Wide
}

public static class ResponsiveLayoutPolicy
{
    public const double MediumMinimumWidth = 760;
    public const double WideMinimumWidth = 1180;

    public static ResponsiveLayoutBand SelectBand(double effectiveWidth)
    {
        if (!double.IsFinite(effectiveWidth) || effectiveWidth < 0)
            throw new ArgumentOutOfRangeException(nameof(effectiveWidth));
        return effectiveWidth >= WideMinimumWidth
            ? ResponsiveLayoutBand.Wide
            : effectiveWidth >= MediumMinimumWidth
                ? ResponsiveLayoutBand.Medium
                : ResponsiveLayoutBand.Narrow;
    }
}
