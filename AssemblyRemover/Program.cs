using dnlib.DotNet;
using dnlib.DotNet.Emit;
using System;
using System.IO;
using System.Reflection;
using dnlib.DotNet.MD;
using dnlib.IO;
using System.Linq;

namespace AssemblyRemover
{
    public static class Ext
    {
        public static Span<byte> GetData(this MemoryImageStream table, int offset = 0)
        {
            var dataField = table.GetType()
                .GetField("data", BindingFlags.NonPublic | BindingFlags.Instance);

            var offsetField = table.GetType().GetField("dataOffset", BindingFlags.NonPublic | BindingFlags.Instance);

            var array = (byte[])dataField.GetValue(table);
            var offs = (int)(int)offsetField.GetValue(table) + offset;
            return new Span<byte>(array, offs, array.Length - offs);
        }
        
        public static MemoryImageStream GetStream(this MDTable table)
        {
            return (MemoryImageStream)table.GetType()
                .GetProperty("ImageStream", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(table);
        }

        public static MemoryImageStream GetStream(this TablesStream table)
        {
            return (MemoryImageStream)typeof(DotNetStream).GetField("imageStream", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(table);
        }
    }
    
    class Program
    {
        
        static int Main(string[] args)
        {
            if(args.Length < 2)
            {
                Console.WriteLine($"Usage: AssemblyRemover.exe [path] [id] (output_path) (other args)\nRemoves AssemblyRef by id\n\n" +
                    $"Other args:\n" +
                    $"--write count | Used for writing modified count of AssemblyRef rows\n" +
                    $"--dup-from=X | Used for duplicating assembly ref from other id (first is 1), use 0 to fill with null");
                return 0;
            }

            var path = args[0];
            var outputPath = args.Length >= 3 ? args[2] : path;
            var shouldWriteCount = args.Any(x => x.Equals("--write-count", StringComparison.OrdinalIgnoreCase));
            var dupFrom = 1;

            foreach(var a in args)
            {
                if(a.StartsWith("--dup-from=", StringComparison.OrdinalIgnoreCase))
                {
                    dupFrom = Convert.ToInt32(a.Substring("--dup-from=".Length));
                }
            }

            var file = File.ReadAllBytes(path);
            using var dataStream = new MemoryStream(file, 0, file.Length, true, true);
            using var reader = new BinaryReader(dataStream);
            using var writer = new BinaryWriter(dataStream);
            
            var module = ModuleDefMD.Load(file);
            module.LoadEverything();

            var idx = (uint)Convert.ToUInt32(args[1]);
            if (!module.TablesStream.AssemblyRefTable.IsValidRID(idx))
            {
                Console.WriteLine($"Invalid AssemblyRef id {idx}");
                return -2;
            }
            
            var asmRef = module.ResolveAssemblyRef(idx);
            Console.WriteLine($"Removing {asmRef.FullName} reference");
            
            var fileTable = module.MetaData.TablesStream;
            var fileTableStream = fileTable.GetStream();
            //fileTableStream.Position = 24;
            fileTableStream.Position = 24 + (10 * 4);
            var numAssemblyRefsOffset = (long)fileTableStream.FileOffset + fileTableStream.Position;
            
            dataStream.Position = numAssemblyRefsOffset;
            
            var refTable = fileTable.AssemblyRefTable;//.TableInfo
            var refTableStream = refTable.GetStream();
            var rowSize = (int)refTable.RowSize;
            var rowCount = (int)refTable.Rows;
            
            var numAssemblyReadRefsCount = reader.ReadInt32();
            if (rowCount != numAssemblyReadRefsCount)
            {
                Console.WriteLine($"Error, native read from stream for assemblyRef count is different from expected {rowCount} got {numAssemblyReadRefsCount}");
                return -1;
            }

            var data = refTableStream.GetData();
            byte[] rows = new byte[rowCount * rowSize];

            //Copy all rows except idx one.
            int offs = 0;
            for (int i = 0; i < rowCount; i++)
            {
                if (i == (idx - 1))
                {
                    Console.WriteLine($"Skipping {i}");
                    continue;
                }
                
                // Copy N metadata bytes
                data.Slice(i * rowSize, rowSize).CopyTo(
                    new Span<byte>(rows, offs, rowSize)
                    );
                
                offs += rowSize;
            }     
            
            if(dupFrom > 0)
            {
                ((Span<byte>)rows).Slice((dupFrom - 1) * rowSize, rowSize).CopyTo(
                    new Span<byte>(rows, offs, rowSize)
                );
            }

            //Copy data back
            ((Span<byte>)rows).CopyTo(data);
            
            //Modify rows count in file.
            dataStream.Position = numAssemblyRefsOffset;
            
            if(shouldWriteCount)
                writer.Write((int)(rowCount - 1));
            
            Console.WriteLine($"Writing to {outputPath}");
            File.WriteAllBytes(outputPath, file);
            return 1;
        }
    }
}
