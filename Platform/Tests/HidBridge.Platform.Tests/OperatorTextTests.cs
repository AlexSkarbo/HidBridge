using System.Globalization;
using HidBridge.ControlPlane.Web.Localization;
using Xunit;

namespace HidBridge.Platform.Tests;

/// <summary>
/// Verifies the bilingual UI text catalog used by the thin operator shell.
/// </summary>
public sealed class OperatorTextTests
{
    /// <summary>
    /// Verifies that English remains the default fallback culture.
    /// </summary>
    [Fact]
    public void Get_UsesEnglishFallbackForUnsupportedCulture()
    {
        var localizer = new OperatorText();
        var originalCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("de-DE");

            Assert.Equal("Fleet Overview", localizer["NavFleetOverview"]);
            Assert.Equal("en", localizer.CurrentCultureCode);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    /// <summary>
    /// Verifies that Ukrainian strings are returned when the request culture is Ukrainian.
    /// </summary>

    /// <summary>
    /// Verifies that theme-related settings labels exist in both supported locales.
    /// </summary>
    [Fact]
    public void Get_ResolvesThemePreferenceLabels()
    {
        var localizer = new OperatorText();
        var originalCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("en-US");
            Assert.Equal("Theme Preference", localizer["ThemePreference"]);
            Assert.Equal("Automatic", localizer["ThemeAuto"]);

            CultureInfo.CurrentUICulture = new CultureInfo("uk-UA");
            Assert.Equal("Налаштування теми", localizer["ThemePreference"]);
            Assert.Equal("Автоматично", localizer["ThemeAuto"]);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }

    [Fact]
    public void Get_UsesUkrainianTextsWhenCultureIsUkrainian()
    {
        var localizer = new OperatorText();
        var originalCulture = CultureInfo.CurrentUICulture;

        try
        {
            CultureInfo.CurrentUICulture = new CultureInfo("uk-UA");

            Assert.Equal("Огляд флоту", localizer["NavFleetOverview"]);
            Assert.Equal("uk", localizer.CurrentCultureCode);
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
        }
    }
}
