/************************************************************************
 * Some of the Bitmap/ExportTexture uses code taken from DerethForever.
 * http://www.derethforever.com
 *
 * DerethForever is licensed under the GNU General Public License
 * http://www.gnu.org/licenses/
 ************************************************************************/
using ACE.Entity.Enum;
using System;
using System.Collections.Generic;
using System.IO;
using System.Drawing;
using System.Drawing.Imaging;

namespace ACE.DatLoader.FileTypes
{
    [DatFileType(DatFileType.Texture)]
    public class Texture : FileType
    {
        public int Unknown { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public SurfacePixelFormat Format { get; set; }
        public int Length { get; set; }
        public byte[] SourceData { get; set; }
        public uint? DefaultPaletteId { get; set; }

        // Used to store a custom palette. Each Key represents the PaletteIndex and the Value is the color.
        // This is used if you want to apply a non-default Palette to the image prior to extraction
        public Dictionary<int, uint> CustomPaletteColors = new Dictionary<int, uint>();

        public override void Unpack(BinaryReader reader)
        {
            Id = reader.ReadUInt32();
            Unknown = reader.ReadInt32();
            Width = reader.ReadInt32();
            Height = reader.ReadInt32();
            Format = (SurfacePixelFormat)reader.ReadUInt32();
            Length = reader.ReadInt32();

            SourceData = reader.ReadBytes(Length);

            switch (Format)
            {
                case SurfacePixelFormat.PFID_INDEX16:
                case SurfacePixelFormat.PFID_P8:
                    DefaultPaletteId = reader.ReadUInt32();
                    break;
                default:
                    DefaultPaletteId = null;
                    break;
            }
        }

        /// <summary>
        /// Exports RenderSurface to a image file
        /// </summary>
        public void ExportTexture(string directory)
        {
            if (Length == 0) return;

            switch (Format)
            {
                case SurfacePixelFormat.PFID_CUSTOM_RAW_JPEG:
                    {
                        string filename = Path.Combine(directory, Id.ToString("X8") + ".jpg");
                        using (BinaryWriter writer = new BinaryWriter(File.Open(filename, FileMode.Create)))
                        {
                            writer.Write(SourceData);
                        }
                    }
                    break;

                default:
                    {
                        var bitmapImage = GetBitmap();
                        string filename = Path.Combine(directory, Id.ToString("X8") + ".png");
                        bitmapImage.Save(filename, ImageFormat.Png);
                    }
                    break;
            }
        }

        /// <summary>
        /// The decoded pixels as straight (not premultiplied) RGBA, 4 bytes per pixel, rows top to bottom. Plain C#, no System.Drawing, so it runs on any platform.
        /// </summary>
        /// <param name="palette">for palette-indexed formats (P8, INDEX16): the palette to use. Without one, the texture's default palette is read from DatManager.PortalDat.
        /// CustomPaletteColors are applied on top of it, and the palette itself is left unchanged.</param>
        /// <exception cref="NotSupportedException">JPEG textures, which are stored compressed and need an image decoder</exception>
        public byte[] GetPixels(Palette palette = null)
        {
            var pixels = new byte[Width * Height * 4];

            void Set(int index, int a, int r, int g, int b)
            {
                pixels[index * 4] = (byte)r;
                pixels[index * 4 + 1] = (byte)g;
                pixels[index * 4 + 2] = (byte)b;
                pixels[index * 4 + 3] = (byte)a;
            }

            switch (Format)
            {
                case SurfacePixelFormat.PFID_CUSTOM_RAW_JPEG:
                    throw new NotSupportedException($"Texture {Id:X8} is a JPEG");

                // DxtUtil already decodes to RGBA
                case SurfacePixelFormat.PFID_DXT1:
                    return DxtUtil.DecompressDxt1(SourceData, Width, Height);
                case SurfacePixelFormat.PFID_DXT3:
                    return DxtUtil.DecompressDxt3(SourceData, Width, Height);
                case SurfacePixelFormat.PFID_DXT5:
                    return DxtUtil.DecompressDxt5(SourceData, Width, Height);
            }

            List<int> colorArray = GetImageColorArray();
            if (colorArray.Count == 0)
                return pixels;

            switch (Format)
            {
                case SurfacePixelFormat.PFID_R8G8B8:
                case SurfacePixelFormat.PFID_CUSTOM_LSCAPE_R8G8B8:
                    for (int i = 0; i < Width * Height; i++)
                        Set(i, 0xFF, (colorArray[i] & 0xFF0000) >> 16, (colorArray[i] & 0xFF00) >> 8, colorArray[i] & 0xFF);
                    break;
                case SurfacePixelFormat.PFID_A8R8G8B8:
                    for (int i = 0; i < Width * Height; i++)
                        Set(i, (int)((colorArray[i] & 0xFF000000) >> 24), (colorArray[i] & 0xFF0000) >> 16, (colorArray[i] & 0xFF00) >> 8, colorArray[i] & 0xFF);
                    break;
                case SurfacePixelFormat.PFID_INDEX16:
                case SurfacePixelFormat.PFID_P8:
                    {
                        palette = palette ?? DatManager.PortalDat?.ReadFromDat<Palette>((uint)DefaultPaletteId)
                            ?? throw new InvalidOperationException($"Texture {Id:X8} is palette-indexed: pass its palette, or initialize DatManager");

                        // a copy, so custom colors never change the (cached) palette
                        var colors = new List<uint>(palette.Colors);
                        foreach (KeyValuePair<int, uint> entry in CustomPaletteColors)
                            if (entry.Key >= 0 && entry.Key < colors.Count)
                                colors[entry.Key] = entry.Value;

                        for (int i = 0; i < Width * Height; i++)
                        {
                            var color = colors[colorArray[i]];
                            Set(i, (int)(color >> 24), (int)(color >> 16) & 0xFF, (int)(color >> 8) & 0xFF, (int)color & 0xFF);
                        }
                    }
                    break;
                case SurfacePixelFormat.PFID_A8:
                case SurfacePixelFormat.PFID_CUSTOM_LSCAPE_ALPHA:
                    for (int i = 0; i < Width * Height; i++)
                        Set(i, 0xFF, colorArray[i], colorArray[i], colorArray[i]);
                    break;
                case SurfacePixelFormat.PFID_R5G6B5: // 16-bit RGB
                    for (int i = 0; i < Width * Height; i++)
                        Set(i, 0xFF, colorArray[3 * i], colorArray[3 * i + 1], colorArray[3 * i + 2]);
                    break;
                case SurfacePixelFormat.PFID_A4R4G4B4:
                    for (int i = 0; i < Width * Height; i++)
                        Set(i, colorArray[4 * i], colorArray[4 * i + 1], colorArray[4 * i + 2], colorArray[4 * i + 3]);
                    break;
            }

            return pixels;
        }

        /// <summary>
        /// Reads RenderSurface to bitmap structure. Windows only (System.Drawing); GetPixels works everywhere.
        /// </summary>
        public Bitmap GetBitmap()
        {
            if (Format == SurfacePixelFormat.PFID_CUSTOM_RAW_JPEG)
            {
                var stream = new MemoryStream(SourceData);
                var image = Image.FromStream(stream);
                return new Bitmap(image);
            }

            var pixels = GetPixels();

            Bitmap bitmap = new Bitmap(Width, Height);
            for (int i = 0; i < Height; i++)
                for (int j = 0; j < Width; j++)
                {
                    int idx = 4 * ((i * Width) + j);
                    bitmap.SetPixel(j, i, Color.FromArgb(pixels[idx + 3], pixels[idx], pixels[idx + 1], pixels[idx + 2]));
                }

            return bitmap;
        }

        /// <summary>
        /// Converts the byte array SourceData into color values per pixel
        /// </summary>
        private List<int> GetImageColorArray()
        {
            List<int> colors = new List<int>();
            if (Length == 0) return colors;

            switch (Format)
            {
                case SurfacePixelFormat.PFID_R8G8B8: // RGB
                    using (BinaryReader reader = new BinaryReader(new MemoryStream(SourceData)))
                    {
                        for (uint i = 0; i < Height; i++)
                            for (uint j = 0; j < Width; j++)
                            {
                                byte b = reader.ReadByte();
                                byte g = reader.ReadByte();
                                byte r = reader.ReadByte();
                                int color = (r << 16) | (g << 8) | b;
                                colors.Add(color);
                            }
                    }
                    break;
                case SurfacePixelFormat.PFID_CUSTOM_LSCAPE_R8G8B8:
                    using (BinaryReader reader = new BinaryReader(new MemoryStream(SourceData)))
                    {
                        for (uint i = 0; i < Height; i++)
                            for (uint j = 0; j < Width; j++)
                            {
                                byte r = reader.ReadByte();
                                byte g = reader.ReadByte();
                                byte b = reader.ReadByte();
                                int color = (r << 16) | (g << 8) | b;
                                colors.Add(color);
                            }
                    }
                    break;
                case SurfacePixelFormat.PFID_A8R8G8B8: // ARGB format. Most UI textures fall into this category
                    using (BinaryReader reader = new BinaryReader(new MemoryStream(SourceData)))
                    {
                        for (uint i = 0; i < Height; i++)
                            for (uint j = 0; j < Width; j++)
                                colors.Add(reader.ReadInt32());
                    }
                    break;
                case SurfacePixelFormat.PFID_INDEX16: // 16-bit indexed colors. Index references position in a palette;
                    using (BinaryReader reader = new BinaryReader(new MemoryStream(SourceData)))
                    {
                        for (uint y = 0; y < Height; y++)
                            for (uint x = 0; x < Width; x++)
                                colors.Add(reader.ReadInt16());
                    }
                    break;
                case SurfacePixelFormat.PFID_A8: // Greyscale, also known as Cairo A8.
                case SurfacePixelFormat.PFID_CUSTOM_LSCAPE_ALPHA:
                    using (BinaryReader reader = new BinaryReader(new MemoryStream(SourceData)))
                    {
                        for (uint y = 0; y < Height; y++)
                            for (uint x = 0; x < Width; x++)
                                colors.Add(reader.ReadByte());
                    }
                    break;
                case SurfacePixelFormat.PFID_P8: // Indexed
                    using (BinaryReader reader = new BinaryReader(new MemoryStream(SourceData)))
                    {
                        for (uint y = 0; y < Height; y++)
                            for (uint x = 0; x < Width; x++)
                                colors.Add(reader.ReadByte());
                    }
                    break;
                case SurfacePixelFormat.PFID_R5G6B5: // 16-bit RGB
                    using (BinaryReader reader = new BinaryReader(new MemoryStream(SourceData)))
                    {
                        for (uint y = 0; y < Height; y++)
                            for (uint x = 0; x < Width; x++)
                            {
                                ushort val = reader.ReadUInt16();
                                List<int> color = get565RGB(val);
                                colors.Add(color[0]); // Red
                                colors.Add(color[1]); // Green
                                colors.Add(color[2]); // Blue
                            }
                    }
                    break;
                case SurfacePixelFormat.PFID_A4R4G4B4:
                    using (BinaryReader reader = new BinaryReader(new MemoryStream(SourceData)))
                    {
                        for (uint y = 0; y < Height; y++)
                            for (uint x = 0; x < Width; x++)
                            {
                                ushort val = reader.ReadUInt16();
                                // Expand each 4-bit channel to 8 bits. Dividing first is
                                // integer division: nibble / 0xF is 0 for every nibble
                                // 0..14 and 1 only for 15, so every channel collapsed to
                                // 0 or 255. 255 / 15 == 17 exactly, so multiplying by 17
                                // maps 0..15 onto 0..255 with no rounding error.
                                int alpha = (val >> 12 & 0xF) * 17;
                                int red = (val >> 8 & 0xF) * 17;
                                int green = (val >> 4 & 0xF) * 17;
                                int blue = (val & 0xF) * 17;

                                colors.Add(alpha);
                                colors.Add(red);
                                colors.Add(green);
                                colors.Add(blue);
                            }
                    }
                    break;
                default:
                    Console.WriteLine("Unhandled SurfacePixelFormat (" + Format.ToString() + ") in RenderSurface " + Id.ToString("X8"));
                    break;
            }

            return colors;
        }

        private List<int> GetPaletteIndexes()
        {
            List<int> colors = new List<int>();
            using (BinaryReader reader = new BinaryReader(new MemoryStream(SourceData)))
            {
                for (uint y = 0; y < Height; y++)
                    for (uint x = 0; x < Width; x++)
                        colors.Add(reader.ReadInt16());
            }
            return colors;
        }

        // https://docs.microsoft.com/en-us/windows/desktop/DirectShow/working-with-16-bit-rgb
        private List<int> get565RGB(ushort val)
        {
            List<int> color = new List<int>();

            int red_mask = 0xF800;
            int green_mask = 0x7E0;
            int blue_mask = 0x1F;

            int r5 = (val & red_mask) >> 11;   // 0..31
            int g6 = (val & green_mask) >> 5;  // 0..63
            int b5 = val & blue_mask;          // 0..31

            // Expand by bit replication rather than a bare shift. A plain v << 3
            // maps 0..31 onto 0,8,16..248 and can never reach 255, so a fully
            // saturated R5G6B5 white decoded to (248,252,248).
            int red = (r5 << 3) | (r5 >> 2);
            int green = (g6 << 2) | (g6 >> 4);
            int blue = (b5 << 3) | (b5 >> 2);

            color.Add(red); // Red
            color.Add(green); // Green
            color.Add(blue); // Blue

            return color;
        }
    }
}
