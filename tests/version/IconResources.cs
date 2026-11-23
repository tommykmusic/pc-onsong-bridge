using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Forms;

internal static class IconResources
{
    private const int IconType = 3;
    private const int IconGroupType = 14;
    private const uint LoadLibraryAsDatafile = 2;
    private delegate bool ResourceNameCallback(IntPtr module, IntPtr type, IntPtr name, IntPtr parameter);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr LoadLibraryEx(string file, IntPtr reserved, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumResourceNames(IntPtr module, IntPtr type, ResourceNameCallback callback, IntPtr parameter);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindResource(IntPtr module, IntPtr name, IntPtr type);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LoadResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LockResource(IntPtr loaded);
    [DllImport("kernel32.dll")]
    private static extern uint SizeofResource(IntPtr module, IntPtr resource);
    [DllImport("kernel32.dll")]
    private static extern bool FreeLibrary(IntPtr module);

    private static byte[] ResourceBytes(IntPtr module, IntPtr resource)
    {
        if (resource == IntPtr.Zero) throw new InvalidDataException("Missing Windows icon resource.");
        IntPtr address = LockResource(LoadResource(module, resource));
        int size = checked((int)SizeofResource(module, resource));
        if (address == IntPtr.Zero || size == 0) throw new InvalidDataException("Empty Windows icon resource.");
        byte[] data = new byte[size];
        Marshal.Copy(address, data, 0, size);
        return data;
    }

    internal static string VerifyExecutable(string executable, byte[] source)
    {
        IntPtr module = LoadLibraryEx(executable, IntPtr.Zero, LoadLibraryAsDatafile);
        if (module == IntPtr.Zero) throw new IOException("Could not read Windows resources from " + executable);
        try
        {
            IntPtr groupResource = IntPtr.Zero;
            EnumResourceNames(module, new IntPtr(IconGroupType), delegate(IntPtr image, IntPtr type, IntPtr name, IntPtr unused)
            {
                groupResource = FindResource(image, name, type);
                return false; // The first group is the executable's primary icon.
            }, IntPtr.Zero);
            byte[] group = ResourceBytes(module, groupResource);
            int count = BitConverter.ToUInt16(source, 4);
            if (source.Length < 6 + count * 16 || group.Length != 6 + count * 14 ||
                !source.Take(6).SequenceEqual(group.Take(6)) || BitConverter.ToUInt16(source, 2) != 1)
                throw new InvalidDataException("The executable icon directory does not match the source ICO.");
            var sizes = new SortedSet<int>();
            // ICO stores a file offset per image. A Windows RT_GROUP_ICON references separate RT_ICON resources.
            for (int i = 0; i < count; i++)
            {
                int fileEntry = 6 + 16 * i;
                int groupEntry = 6 + 14 * i;
                int length = BitConverter.ToInt32(source, fileEntry + 8);
                int offset = BitConverter.ToInt32(source, fileEntry + 12);
                int resourceId = BitConverter.ToUInt16(group, groupEntry + 12);
                byte[] embedded = ResourceBytes(module, FindResource(module, new IntPtr(resourceId), new IntPtr(IconType)));
                if (source[fileEntry] != group[groupEntry] || source[fileEntry + 1] != group[groupEntry + 1] ||
                    BitConverter.ToInt32(group, groupEntry + 8) != length || embedded.Length != length ||
                    !source.Skip(offset).Take(length).SequenceEqual(embedded))
                    throw new InvalidDataException("The Windows icon frame differs at entry " + i + " in " + executable);
                sizes.Add(source[fileEntry] == 0 ? 256 : source[fileEntry]);
            }
            if (sizes.Count < 2) throw new InvalidDataException("The app icon must include multiple resolutions.");
            return String.Join(", ", sizes);
        }
        finally { FreeLibrary(module); }
    }

    internal static bool FormShowsIcon(Form form, string iconPath)
    {
        if (form.Icon == null || !form.ShowIcon) return false;
        using (var icon = new Icon(iconPath, form.Icon.Size))
        using (var expected = icon.ToBitmap())
        using (var actual = form.Icon.ToBitmap())
        {
            if (actual.Size != expected.Size) return false;
            for (int y = 0; y < actual.Height; y++)
                for (int x = 0; x < actual.Width; x++)
                    if (actual.GetPixel(x, y).ToArgb() != expected.GetPixel(x, y).ToArgb()) return false;
            return true;
        }
    }
}
