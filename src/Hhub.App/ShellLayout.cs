namespace Hhub.App;

// Logical pixels: keep shell dimensions independent from device DPI and plugin content.
internal static class ShellLayout
{
    public const double SidebarExpandedWidth = 208;
    public const double SidebarCollapsedWidth = 52;
    public const double NavigationIconSize = 20;
    public const double NavigationFontSize = 14;
    public const double NavigationRowHeight = 44;
    public const double BrandIconSize = 28;
    public const double TitleFontSize = 24;
    public const double BodyFontSize = 14;
    public const double HeadingHeight = 36;
    public const double HeadingGap = 6;
    public const double SectionGap = 16;
    public const double RowGap = 8;
    public const double PageInset = 16;
    public static double PageInsetForWidth(double width) => width <= 0 ? PageInset : width < 900 ? 8 : width < 1100 ? 12 : PageInset;
    public const double SceneTileSize = 112;
    public const double SceneTileHeight = SceneTileSize;
}
