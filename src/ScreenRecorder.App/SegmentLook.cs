using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace ScreenRecorder.App;

static class SegmentLook
{
    // The probe's brushes follow the window theme, unlike Application.Current.Resources.
    public static void Apply(Button segment, bool on, Border probe)
    {
        var off = (Brush)Application.Current.Resources["SubtleFillColorTransparentBrush"];
        segment.Background = on ? probe.Background : off;
        segment.BorderBrush = on ? probe.BorderBrush : off;
    }
}
