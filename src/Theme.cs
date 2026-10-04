using System.Windows;
using System.Windows.Media;
namespace PgrVoice;
internal static class Theme
{
    public static Brush Brush(string key) => (Brush)Application.Current.FindResource(key);
}
