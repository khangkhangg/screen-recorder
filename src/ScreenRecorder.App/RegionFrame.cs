using Windows.Graphics;

namespace ScreenRecorder.App;

// Which edges a drag on the frame moves. Corners are two edges, and the tab moves the whole region.
[Flags]
public enum FrameGrip { None = 0, Left = 1, Top = 2, Right = 4, Bottom = 8, Move = 16 }

// Corner marks are in pairs per corner (top-left, top-right, bottom-right, bottom-left), arm along the edge first.
// Handles go top, right, bottom, left. Grabs come in hit-test order, the tab first.
public sealed record FrameLayout(RectInt32[] Strips, RectInt32[] Marks, RectInt32[] Handles, RectInt32 Tab, bool TabInside,
    (FrameGrip Grip, RectInt32 Area)[] Grabs);

// The region frame in screen pixels. Sizes are in DIPs times the monitor scale, and every part is shifted onto the
// monitor, so a side that touches a monitor edge is drawn just inside the region.
public static class RegionFrame
{
    public const int MinSize = 64;
    static readonly (bool Left, bool Top)[] Corners = [(true, true), (false, true), (false, false), (true, false)];

    // The X sits in the last square of the tab.
    public static bool OnTabClose(FrameLayout l, int x, int y) =>
        x >= l.Tab.X + l.Tab.Width - l.Tab.Height && x < l.Tab.X + l.Tab.Width && y >= l.Tab.Y && y < l.Tab.Y + l.Tab.Height;

    public static FrameLayout Layout(RectInt32 region, RectInt32 monitor, double scale, int tabWidth)
    {
        int Px(double v) => (int)Math.Round(v * scale);
        int t = Px(2), mark = Px(6), along = Px(20), handle = Px(20), pill = Px(10), grab = Px(24), handleGrab = Px(40), tabHeight = Px(30);
        int right = region.X + region.Width, bottom = region.Y + region.Height, cx = region.X + region.Width / 2, cy = region.Y + region.Height / 2;
        RectInt32 Fit(int x, int y, int w, int h) =>
            new(Math.Max(monitor.X, Math.Min(x, monitor.X + monitor.Width - w)), Math.Max(monitor.Y, Math.Min(y, monitor.Y + monitor.Height - h)), w, h);
        // an L outside one corner: thick across, reaching `along` past the corner on both edges
        RectInt32[] L(bool left, bool top, int thick) =>
        [
            Fit(left ? region.X - thick : right - along, top ? region.Y - thick : bottom, thick + along, thick),
            Fit(left ? region.X - thick : right, top ? region.Y - thick : bottom - along, thick, thick + along),
        ];

        var (strips, _) = TargetPicker.Outline(region, monitor, t, 0);
        RectInt32[] handles =
        [
            Fit(cx - handle / 2, strips[0].Y + t / 2 - pill / 2, handle, pill),
            Fit(strips[3].X + t / 2 - pill / 2, cy - handle / 2, pill, handle),
            Fit(cx - handle / 2, strips[1].Y + t / 2 - pill / 2, handle, pill),
            Fit(strips[2].X + t / 2 - pill / 2, cy - handle / 2, pill, handle),
        ];
        var w = Math.Min(tabWidth, region.Width);
        var inside = strips[0].Y - tabHeight < monitor.Y;
        var tab = Fit(Math.Max(region.X, Math.Min(region.X + Px(30), right - w)), inside ? strips[0].Y + strips[0].Height : strips[0].Y - tabHeight, w, tabHeight);

        var grabs = new List<(FrameGrip, RectInt32)> { (FrameGrip.Move, tab) };
        foreach (var (left, top) in Corners)
            foreach (var r in L(left, top, grab))
                grabs.Add(((left ? FrameGrip.Left : FrameGrip.Right) | (top ? FrameGrip.Top : FrameGrip.Bottom), r));
        grabs.Add((FrameGrip.Top, Fit(cx - handleGrab / 2, region.Y - grab, handleGrab, grab)));
        grabs.Add((FrameGrip.Right, Fit(right, cy - handleGrab / 2, grab, handleGrab)));
        grabs.Add((FrameGrip.Bottom, Fit(cx - handleGrab / 2, bottom, handleGrab, grab)));
        grabs.Add((FrameGrip.Left, Fit(region.X - grab, cy - handleGrab / 2, grab, handleGrab)));

        return new(strips, [.. Corners.SelectMany(c => L(c.Left, c.Top, mark))], handles, tab, inside, [.. grabs]);
    }

    public static FrameGrip HitTest(FrameLayout layout, int x, int y) =>
        layout.Grabs.FirstOrDefault(g => x >= g.Area.X && x < g.Area.X + g.Area.Width && y >= g.Area.Y && y < g.Area.Y + g.Area.Height).Grip;

    // Grip and icon always show. Widths are in the same unit as the region width.
    public static (bool Name, bool Size) TabContent(int regionWidth, int baseWidth, int nameWidth, int sizeWidth) =>
        (baseWidth + nameWidth <= regionWidth, baseWidth + nameWidth + sizeWidth <= regionWidth);

    public static RectInt32 Move(RectInt32 start, int dx, int dy, RectInt32 monitor) =>
        new(Math.Max(monitor.X, Math.Min(start.X + dx, monitor.X + monitor.Width - start.Width)),
            Math.Max(monitor.Y, Math.Min(start.Y + dy, monitor.Y + monitor.Height - start.Height)), start.Width, start.Height);

    // The edges that the grip does not name stay where they were.
    public static RectInt32 Resize(RectInt32 start, FrameGrip grip, int dx, int dy, RectInt32 monitor)
    {
        int l = start.X, t = start.Y, r = start.X + start.Width, b = start.Y + start.Height;
        if (grip.HasFlag(FrameGrip.Left)) l = Math.Max(monitor.X, Math.Min(l + dx, r - MinSize));
        if (grip.HasFlag(FrameGrip.Right)) r = Math.Min(monitor.X + monitor.Width, Math.Max(r + dx, l + MinSize));
        if (grip.HasFlag(FrameGrip.Top)) t = Math.Max(monitor.Y, Math.Min(t + dy, b - MinSize));
        if (grip.HasFlag(FrameGrip.Bottom)) b = Math.Min(monitor.Y + monitor.Height, Math.Max(b + dy, t + MinSize));
        return new(l, t, r - l, b - t);
    }
}
