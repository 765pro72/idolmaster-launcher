// Native GDI+ paths from the themed tracing of the supplied raster logo.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
internal static class CgssWordmark {
    internal sealed class Part : IDisposable {
        internal readonly GraphicsPath Path;internal readonly Color Ink;
        internal Part(GraphicsPath path,Color ink){Path=path;Ink=ink;}
        public void Dispose(){Path.Dispose();}
    }
    internal static List<Part> Create(){
        Color[] palette={Color.FromArgb(82,107,126),Color.FromArgb(101,127,145),Color.FromArgb(255,249,237),Color.FromArgb(189,155,105)};
        List<Part> parts=new List<Part>();
        try{
            using(Stream stream=typeof(CgssWordmark).Assembly.GetManifestResourceStream("cgss-wordmark.paths")){
                if(stream==null)throw new InvalidOperationException("CGSS vector wordmark resource is missing.");
                using(BinaryReader reader=new BinaryReader(stream)){
                    if(new string(reader.ReadChars(4))!="CGW1")throw new InvalidDataException("Invalid CGSS vector wordmark.");
                    int count=reader.ReadUInt16();
                    for(int i=0;i<count;i++){
                        int tone=reader.ReadByte(),length=reader.ReadUInt16();
                        if(tone>=palette.Length||length<2)throw new InvalidDataException("Invalid CGSS wordmark path.");
                        PointF[] points=new PointF[length];byte[] types=new byte[length];
                        for(int j=0;j<length;j++){points[j]=new PointF(reader.ReadSingle(),reader.ReadSingle());types[j]=reader.ReadByte();}
                        parts.Add(new Part(new GraphicsPath(points,types,FillMode.Winding),palette[tone]));
                    }
                }
            }
            return parts;
        }catch{foreach(Part part in parts)part.Dispose();throw;}
    }
}
