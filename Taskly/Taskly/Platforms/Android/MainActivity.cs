// Hlavní aktivita pro Android verzi aplikace Taskly
using Android.App;
using Android.Content.PM;
using Android.OS;

namespace Taskly.Platforms.Android
{
    // Konfigurace aktivity - vstupní bod aplikace
    [Activity(Theme = "@style/Maui.MainTheme", MainLauncher = true,
        ConfigurationChanges = ConfigChanges.ScreenSize |
                               ConfigChanges.Orientation |
                               ConfigChanges.UiMode |
                               ConfigChanges.ScreenLayout |
                               ConfigChanges.SmallestScreenSize |
                               ConfigChanges.Density)]
    public class MainActivity : MauiAppCompatActivity
    {
        // Spustí se automaticky při otevření aplikace - jako "main()" funkce
        protected override void OnCreate(Bundle? savedInstanceState)
        {
            // Spuštění aplikace
            base.OnCreate(savedInstanceState);
        }
    }
}