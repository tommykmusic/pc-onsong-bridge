using System;
using PcOnSongBridge;

internal static class ViewerTests
{
    private const string Package = "com.onsongapp.app";
    private const string Zero = "[0,0][0,0]";

    private static string Node(string id, string bounds, string children = "", string extras = "")
    {
        return "<node package=\"" + Package + "\" resource-id=\"" + id + "\" bounds=\"" + bounds + "\" " + extras + ">" + children + "</node>";
    }

    private static string Dump(int rotation, int width, int height, string overlay = "", bool ambiguous = false, string chartChildren = "")
    {
        string area = "[0,0][" + width + "," + height + "]";
        string chart = Node("_aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "[0,136][" + width + "," + height + "]", chartChildren);
        string page = Node("_sheet_53", area, chart);
        string hidden = Node("_sheet_54", ambiguous ? area : Zero);
        string viewer = Node("viewer", area, Node("R2-Scroller", area, hidden + page + Node("_sheet_52", Zero)));
        return "XML header\r\n<hierarchy rotation=\"" + rotation + "\">" + Node("", area, viewer + overlay)
            + "</hierarchy>\nUI hierarchy dumped to: /dev/tty";
    }

    private static string Windows(int rotation, int width, int height)
    {
        return "  Display: mDisplayId=0 (organized)\r\n    init=1600x2400 320dpi cur=" + width + "x" + height + " app=" + width + "x" + height
            + "\r\n  DisplayRotation\r\n    mLastOrientation=-1\r\n    mRotation=" + rotation + " mDeferredRotationPauseCount=0\r\n  mTopFocusedDisplayId=0";
    }

    private static void Reject(Action action)
    {
        try { action(); }
        catch (InvalidOperationException error)
        {
            if (error.Message.Contains("private song text")) throw new Exception("Leaked chart text into errors.");
            return;
        }
        throw new Exception("Expected the viewer check to refuse this layout.");
    }

    private static void Main()
    {
        string portrait = Dump(0, 1600, 2400, Node("toolbar", "[0,0][1600,136]"));
        SongViewer page = SongViewer.Read(portrait);
        page.CheckCurrentDisplay(Windows(0, 1600, 2400));
        if (page.Gesture("next") != "1152 1200 448 1200 300" || page.Gesture("previous") != "448 1200 1152 1200 300")
            throw new Exception("Portrait swipes differ from the rig's tested gestures.");
        string originalIdentity = page.Identity;
        var scroll = SongViewer.Read(portrait, true);
        if (scroll.Gesture("scroll-down") != "800 1728 800 672 300" || scroll.Gesture("scroll-up") != "800 672 800 1728 300")
            throw new Exception("Scroll gesture should move the content opposite the reading direction.");
        if (SongViewer.Read(portrait.Replace("_sheet_53", "_sheet_55")).IsAdjacentTo(originalIdentity, "next") ||
            page.IsAdjacentTo(originalIdentity, "next") ||
            !SongViewer.Read(portrait.Replace("_sheet_53", "_sheet_54")).IsAdjacentTo(originalIdentity, "next"))
            throw new Exception("A timed song test must observe the adjacent sheet, including repeated charts.");
        string blockedVertical = Dump(0, 1600, 2400, Node("menu", "[760,700][840,760]"));
        Reject(() => SongViewer.Read(blockedVertical, true));
        SongViewer.Read(blockedVertical, false);
        foreach (int angle in new[] { 1, 3 })
        {
            page = SongViewer.Read(Dump(angle, 2400, 1600));
            page.CheckCurrentDisplay(Windows(angle, 2400, 1600));
            if (page.Gesture("next") != "1728 800 672 800 300") throw new Exception("Landscape coordinates were transformed twice.");
            if (SongViewer.Read(Dump(angle, 2400, 1600), true).Gesture("scroll-down") != "1200 1152 1200 448 300")
                throw new Exception("Landscape scroll axis was transformed twice.");
            Reject(() => page.CheckCurrentDisplay(Windows((angle + 2) % 4, 2400, 1600)));
            Reject(() => page.CheckCurrentDisplay(Windows(angle, 1600, 2400)));
            Reject(() => page.CheckCurrentDisplay(Windows(angle, 2400, 1600).Replace("mTopFocusedDisplayId=0", "mTopFocusedDisplayId=1")));
        }
        Reject(() => SongViewer.Read(portrait.Replace("viewer", "private song text")));
        Reject(() => SongViewer.Read(portrait.Replace("_aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", "unknown-chart")));
        Reject(() => SongViewer.Read(portrait.Replace(Package, "other.app")));
        Reject(() => SongViewer.Read(Dump(0, 1600, 2400, "", true)));
        Reject(() => SongViewer.Read(Dump(0, 1600, 2400, Node("menu", "[520,1140][670,1250]"))));
        Reject(() => SongViewer.Read(Dump(0, 1600, 2400, "", false, Node("action", "[900,1100][1200,1400]", "", "clickable=\"true\""))));
        Reject(() => SongViewer.Read(portrait.Replace("[0,136][1600,2400]", "[0,136][400,2400]")));
        Reject(() => SongViewer.Read("<hierarchy rotation=\"0\"><private song text</hierarchy>"));
        Reject(() => SongViewer.Read(portrait + portrait));
        Console.WriteLine("PASS portrait and landscape page/scroll gestures, adjacent chart observations, orientation changes, clipping and overlays.");
    }
}
