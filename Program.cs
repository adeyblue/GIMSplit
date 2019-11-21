using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace GIMSplit
{
    class Program
    {
        enum BlockType : ushort
        {
            ROOT = 2,
            PICTURE = 3,
            IMAGE = 4,
            PALETTE = 5,
            FILEINFO = 0xff
        }

        class BlockHeader
        {
            public BlockType type;
            public ushort firstUnk;
            public uint blockSize;
            public long blockEndLocation;
            // these are absolute, not relative like the file format
            public long nextBlockLocation;
            public long dataBlockLocation;
            public List<BlockHeader> thisLevelBlocks;

            public override string ToString()
            {
                return String.Format(
                    "Type {0}, size {1:x}, End loc {2:x}, Next block at {3:x}, Data at {4:x}",
                    type.ToString(),
                    blockSize,
                    blockEndLocation,
                    nextBlockLocation,
                    dataBlockLocation
                );
            }
        }

        static string OUT_DIR;
        static string FILE_NAME;
        static int FILE_INDEX = 0;

        static readonly PixelFormat[] FORMAT_TYPES = new PixelFormat[8]{
            PixelFormat.Format16bppRgb565,
            PixelFormat.Format16bppArgb1555,
            PixelFormat.Format32bppArgb, // There isn't a rgba4444 pixel format, so we convert to this
            PixelFormat.Format32bppArgb,
            PixelFormat.Format4bppIndexed,
            PixelFormat.Format8bppIndexed,
            PixelFormat.DontCare,
            PixelFormat.DontCare
        };

        static readonly int[] SOURCE_FORMAT_BPP = new int[4]{
            2, 2, 2, 4
        };

        static readonly float[] BPP_MULTIPLIER = new float[8]{
            2, 2, 2, 4, 0.5f, 1, 0, 0
        };

        // format data used - https://www.psdevwiki.com/ps3/Graphic_Image_Map_(GIM)
        static BlockHeader ReadBlockHeader(BinaryReader br)
        {
            long preLoc = br.BaseStream.Position;
            byte[] headerBytes = br.ReadBytes(16);
            ushort type = BitConverter.ToUInt16(headerBytes, 0);
            BlockHeader header = new BlockHeader();
            header.type = (BlockType)type;
            header.firstUnk = BitConverter.ToUInt16(headerBytes, 2);
            // the read moved us 0x10 bytes forward, subtract it so we don't account for it again when seeking
            header.blockSize = BitConverter.ToUInt32(headerBytes, 4);
            header.blockEndLocation = header.blockSize + preLoc;
            header.nextBlockLocation = preLoc + BitConverter.ToUInt32(headerBytes, 8);
            header.dataBlockLocation = preLoc + BitConverter.ToUInt32(headerBytes, 0xc);
            header.thisLevelBlocks = new List<BlockHeader>();
            return header;
        }

        static void DumpImage(BinaryReader br, BlockHeader block, Color[] palette)
        {
            br.BaseStream.Seek(block.dataBlockLocation, SeekOrigin.Begin);
            int dataHeaderSize = br.ReadUInt16();
            byte[] dataHeader = new byte[dataHeaderSize];
            // we've aleady rea the 2 bytes of the size, hence the - 2
            byte[] tempBuffer = br.ReadBytes((int)dataHeaderSize - 2);
            Buffer.BlockCopy(tempBuffer, 0, dataHeader, 2, (int)dataHeaderSize - 2);
            ushort pixFmt = BitConverter.ToUInt16(dataHeader, 4);
            // only dealing with planar and indexed color
            if (pixFmt > 7)
            {
                Console.WriteLine("Found image with unsupported pixel type of {0:x}", pixFmt);
                return;
            }
            if ((pixFmt >= 4) && (palette == null))
            {
                Console.WriteLine("Found paletted image type but no palette supplied. Using whatever is default");
            }
            PixelFormat pf = FORMAT_TYPES[pixFmt];
            int width = BitConverter.ToUInt16(dataHeader, 8);
            int height = BitConverter.ToUInt16(dataHeader, 0xa);
            int pitch = BitConverter.ToUInt16(dataHeader, 0xe);
            int pixelLocation = BitConverter.ToInt32(dataHeader, 0x1c) - dataHeaderSize;
            int pixelEnd = BitConverter.ToInt32(dataHeader, 0x20) - dataHeaderSize;
            int planeMask = BitConverter.ToInt32(dataHeader, 0x24);
            int levelType = BitConverter.ToInt16(dataHeader, 0x28);
            int mipmaps = BitConverter.ToInt16(dataHeader, 0x2a);
            int frameType = BitConverter.ToInt16(dataHeader, 0x2c);
            int frameCount = BitConverter.ToInt16(dataHeader, 0x2e);
            float bppMultiplier = BPP_MULTIPLIER[pixFmt];

            br.BaseStream.Seek(pixelLocation, SeekOrigin.Current);
            using (Bitmap bm = new Bitmap(width, height, pf))
            {
                BitmapData bmData;
                Rectangle rc = new Rectangle(0, 0, width, height);
                bmData = bm.LockBits(rc, ImageLockMode.WriteOnly, pf);
                if (palette != null)
                {
                    Color[] origPalette = bm.Palette.Entries;
                    Array.Copy(palette, origPalette, Math.Min(origPalette.Length, palette.Length));
                }
                IntPtr pOut = bmData.Scan0;
                int bmPitch = bmData.Stride;
                int sourceWidthBytes = (int)(width * bppMultiplier);
                int pitchAdvance = sourceWidthBytes % pitch;
                for (int y = 0; y < height; ++y)
                {
                    byte[] sourceLine = br.ReadBytes(sourceWidthBytes);
                    int bytesToCopy = sourceWidthBytes;
                    if (pixFmt == 2)
                    {
                        // source is rgba4444, we need to convert to rgba8888
                        int newBytesToCopy = bytesToCopy * 2;
                        byte[] convertedLine = new byte[newBytesToCopy];
                        for (int i = 0; i < bytesToCopy; ++i)
                        {
                            byte srcByte = sourceLine[i];
                            byte lowByte = (byte)((srcByte & 0xF) * 2);
                            byte hiByte = (byte)(((srcByte & 0xF0) >> 4) * 2);
                            convertedLine[i * 2] = lowByte;
                            convertedLine[i * 2 + 1] = hiByte;
                        }
                        sourceLine = convertedLine;
                        bytesToCopy = newBytesToCopy;
                    }
                    Marshal.Copy(sourceLine, 0, pOut, bytesToCopy);
                    // skip any padding
                    if (pitchAdvance != 0)
                    {
                        br.ReadBytes(pitchAdvance);
                    }
                    pOut = new IntPtr(pOut.ToInt64() + bmPitch);
                }
                bm.UnlockBits(bmData);
                string outputFileName = String.Format("{0}{1}{2}-{3}.png", OUT_DIR, Path.DirectorySeparatorChar, FILE_NAME, ++FILE_INDEX);
                Debug.WriteLine(String.Format("Dumping image {0} with pixfmt {1}", outputFileName, pf.ToString()));
                bm.Save(outputFileName, ImageFormat.Png);
            }
        }

        static Color RGB565ToColour(byte[] arr, int location)
        {
            const float scaleFactor5 = 255.0f / 31.0f;
            const float scaleFactor6 = 255.0f / 63.0f;
            byte srcByte = arr[location];
            int lowByte = (int)((srcByte & 0x1F) * scaleFactor5);
            srcByte >>= 5;
            byte srcByte2 = arr[location + 1];
            int secondByte = (int)((((srcByte2 & 0x7) << 3) | srcByte) * scaleFactor6);
            srcByte2 >>= 3;
            int thirdByte = (int)((srcByte2 & 0x1F) * scaleFactor5);
            return Color.FromArgb(thirdByte, secondByte, lowByte);
        }

        static Color RGBA5551ToColour(byte[] arr, int location)
        {
            const float scaleFactor = 255.0f / 31.0f;
            byte srcByte = arr[location];
            int lowByte = (int)((srcByte & 0x1F) * scaleFactor);
            srcByte >>= 5;
            byte srcByte2 = arr[location + 1];
            int secondByte = (int)((((srcByte2 & 0x3) << 3) | srcByte) * scaleFactor);
            srcByte2 >>= 2;
            int thirdByte = (int)((srcByte2 & 0x1F) * scaleFactor);
            int alpha = ((srcByte2 >> 5) != 0) ? 0xff : 0;
            return Color.FromArgb(alpha, thirdByte, secondByte, lowByte);
        }

        static Color RGBA4444ToColour(byte[] arr, int location)
        {
            byte srcByte = arr[location];
            int lowByte = ((srcByte & 0xF) * 2);
            int secondByte = (((srcByte & 0xF0) >> 4) * 2);
            srcByte = arr[location + 1];
            int thirdByte = ((srcByte & 0xF) * 2);
            int topByte = (((srcByte & 0xF0) >> 4) * 2);
            return Color.FromArgb(topByte, thirdByte, secondByte, lowByte);
        }

        static Color RGBA32ToColour(byte[] arr, int location)
        {
            byte[] intArr = new byte[4];
            Buffer.BlockCopy(arr, location, intArr, 0, 4);
            Array.Reverse(intArr);
            //return Color.FromArgb(BitConverter.ToInt32(arr, location));
            return Color.FromArgb(BitConverter.ToInt32(intArr, 0));
        }

        delegate Color BytesToColour(byte[] arr, int location);

        static BytesToColour[] RGB_TO_COLOUR = new BytesToColour[4]{
            RGB565ToColour,
            RGBA5551ToColour,
            RGBA4444ToColour,
            RGBA32ToColour
        };

        static Color[] GetPalette(BinaryReader br, BlockHeader block)
        {
            br.BaseStream.Seek(block.dataBlockLocation, SeekOrigin.Begin);
            int dataHeaderSize = br.ReadUInt16();
            byte[] dataHeader = new byte[dataHeaderSize];
            // we've aleady rea the 2 bytes of the size, hence the - 2
            byte[] tempBuffer = br.ReadBytes((int)dataHeaderSize - 2);
            Buffer.BlockCopy(tempBuffer, 0, dataHeader, 2, (int)dataHeaderSize - 2);
            ushort pixFmt = BitConverter.ToUInt16(dataHeader, 4);
            // only dealing with planar and indexed color
            if (pixFmt >= 4)
            {
                Console.WriteLine("Found paletted image type in palette! This doesn't seem right");
                return null;
            }
            PixelFormat pf = FORMAT_TYPES[pixFmt];
            int width = BitConverter.ToUInt16(dataHeader, 8);
            int pixelLocation = BitConverter.ToInt32(dataHeader, 0x1c) - dataHeaderSize;
            int pixelEnd = BitConverter.ToInt32(dataHeader, 0x20) - dataHeaderSize;
            int bpp = SOURCE_FORMAT_BPP[pixFmt];

            BytesToColour conversionFn = RGB_TO_COLOUR[pixFmt];
            int sourceWidthBytes = (int)(width * bpp);
            Color[] palette = new Color[width];
            br.BaseStream.Seek(pixelLocation, SeekOrigin.Current);
            byte[] sourceLine = br.ReadBytes(sourceWidthBytes);
            for (int i = 0, iter = 0; i < width; ++i, iter += bpp)
            {
                palette[i] = conversionFn(sourceLine, iter);
            }
            return palette;
        }

        static void DumpFileInfo(BinaryReader br, BlockHeader block)
        {
            br.BaseStream.Seek(block.dataBlockLocation, SeekOrigin.Begin);
            int blockSize = (int)(block.blockEndLocation - block.dataBlockLocation);
            string wholeThing = Encoding.UTF8.GetString(br.ReadBytes(blockSize));
            if (!String.IsNullOrEmpty(wholeThing))
            {
                Console.WriteLine("Found file info:");
                string[] parts = wholeThing.Split('\0');
                foreach (string s in parts)
                {
                    Console.WriteLine(s);
                }
            }
        }

        static void ParseBlock(BinaryReader br, BlockHeader block)
        {
            switch (block.type)
            {
                case BlockType.PICTURE:
                    {
                        List<BlockHeader> childBlocks = block.thisLevelBlocks;
                        Color[] palette = null;
                        BlockHeader paletteBlock = childBlocks.Find((x) => { return x.type == BlockType.PALETTE; });
                        if (paletteBlock != null)
                        {
                            palette = GetPalette(br, paletteBlock);
                        }
                        BlockHeader imageBlock = childBlocks.Find((x) => { return x.type == BlockType.IMAGE; });
                        if (imageBlock != null)
                        {
                            DumpImage(br, imageBlock, palette);
                        }
                    }
                    break;
                case BlockType.FILEINFO:
                    {
                        DumpFileInfo(br, block);
                    }
                    break;
            }
        }

        static long GetSubBlockList(BinaryReader br, BlockHeader block, List<BlockHeader> levelBlocks, long containerEndPoint)
        {
            long nextBlockLoc = block.nextBlockLocation;
            while (true)
            {
                BlockHeader nextBlock = ReadBlockHeader(br);
                levelBlocks.Add(nextBlock);
                nextBlockLoc = nextBlock.nextBlockLocation;
                switch (nextBlock.type)
                {
                    case BlockType.ROOT:
                    case BlockType.PICTURE:
                        {
                            // this is a container, recurse
                            br.BaseStream.Seek(nextBlockLoc, SeekOrigin.Begin);
                            nextBlockLoc = GetSubBlockList(br, nextBlock, nextBlock.thisLevelBlocks, nextBlock.blockEndLocation);
                        }
                        break;
                    default:
                        {
                            br.BaseStream.Seek(nextBlock.blockEndLocation, SeekOrigin.Begin);
                        }
                        break;
                }
                if (br.BaseStream.Position >= containerEndPoint)
                {
                    break;
                }
                br.BaseStream.Seek(nextBlockLoc, SeekOrigin.Begin);
            }
            return nextBlockLoc;
        }

        static void DumpEmbeddedImage(BinaryReader br, BlockHeader bh, BlockHeader rootBlock)
        {
            if (bh.type == BlockType.PICTURE)
            {
                MemoryStream ms = new MemoryStream();
                using (BinaryWriter bw = new BinaryWriter(ms))
                {
                    // header line
                    bw.Write(Encoding.UTF8.GetBytes("MIG.00.1PSP\0"));
                    bw.Write(0);
                    
                    uint offset = 0x10;
                    // write the root header
                    bw.Write((ushort)BlockType.ROOT);
                    bw.Write(rootBlock.firstUnk); // unk = 0x10
                    // + 0x10 for the root headers
                    bw.Write(0x10 + bh.blockSize);
                    bw.Write(offset); // next block offset
                    bw.Write(offset); // block data offset
                    // root block header written, now the PICTURE
                    bw.Write((short)BlockType.PICTURE);
                    bw.Write(bh.firstUnk);
                    bw.Write(bh.blockSize);
                    bw.Write(offset);
                    bw.Write(offset);
                    // now write the contents of each block
                    foreach (BlockHeader child in bh.thisLevelBlocks)
                    {
                        bw.Write((ushort)child.type); // type
                        bw.Write(child.firstUnk); // unk
                        bw.Write(child.blockSize); // block size
                        bw.Write(child.blockSize); // next block offset
                        bw.Write(offset); // this block data offset
                        br.BaseStream.Seek(child.dataBlockLocation, SeekOrigin.Begin);
                        byte[] blockContents = br.ReadBytes((int)child.blockSize - 0x10);
                        bw.Write(blockContents);
                    }
                    bw.Flush();
                    string outputFileName = String.Format("{0}{1}{2}-{3}.gim", OUT_DIR, Path.DirectorySeparatorChar, FILE_NAME, ++FILE_INDEX);
                    File.WriteAllBytes(outputFileName, ms.ToArray());
                }
            }
        }

        static void ParseGIMImages(string file)
        {
            byte[] fileData = File.ReadAllBytes(file);
            MemoryStream ms = new MemoryStream(fileData);
            BinaryReader br = new BinaryReader(ms);
            if((new string(br.ReadChars(8)) != "MIG.00.1") || (new string(br.ReadChars(4)) != "PSP\0"))
            {
                Console.WriteLine("{0} isn't a GIM we can process!", file);
                return;
            }
            // unk 4 bytes of file header
            br.ReadBytes(4);
            BlockHeader block = ReadBlockHeader(br);
            if (block.type != BlockType.ROOT)
            {
                Console.WriteLine("First block isn't the root block");
                return;
            }
            br.BaseStream.Seek(block.nextBlockLocation, SeekOrigin.Begin);
            GetSubBlockList(br, block, block.thisLevelBlocks, block.blockEndLocation);
            foreach (BlockHeader bh in block.thisLevelBlocks)
            {
                DumpEmbeddedImage(br, bh, block);
                // This was an attempt to split the Picture blocks into bmp
                // files ourself, it doesn't work
                //ParseBlock(br, bh);
            }
        }

        static void Main(string[] args)
        {
            if (args.Length < 1 || !File.Exists(args[0]))
            {
                Console.WriteLine("Usage: GIMSplit file.gim <outputDir>");
                return;
            }
            string file = args[0];
            if (args.Length == 2)
            {
                OUT_DIR = args[1];
            }
            else
            {
                OUT_DIR = Path.GetDirectoryName(file);
            }
            FILE_NAME = Path.GetFileNameWithoutExtension(file);
            ParseGIMImages(file);
        }
    }
}
