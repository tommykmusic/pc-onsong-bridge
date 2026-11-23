using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

// Test executable placed beside a dummy scrcpy.exe in a fresh directory. Never contacts Android.
internal static class CatalogAdb
{
    private static int Main(string[] args)
    {
        if (!args.Take(3).SequenceEqual(new[] { "-s", "CATALOG_FIXTURE", "shell" })) return 1;
        string request = String.Join(" ", args.Skip(3));
        if (request == "dumpsys window" || request == "dumpsys window displays")
            Console.WriteLine("  mCurrentFocus=Window{1234 u0 com.onsongapp.app/com.onsongapp.android.MainActivity}");
        else if (request == "input help") Console.WriteLine("  keycombination <key codes>");
        else
        {
            if (!Regex.IsMatch(request, @"\Ainput (?:keyevent|keycombination)(?: KEYCODE_[A-Z0-9_]+){1,4}\z")) return 2;
            string path = Path.Combine("receipts", String.Join("+", args.Skip(5)) + ".txt");
            // Repeated dispatch is an error, including concurrent duplicate inputs.
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream)) writer.Write(request);
        }
        return 0;
    }
}
