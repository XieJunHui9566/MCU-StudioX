namespace StudioX.Application.OpenOcdPlot;

using System.Buffers.Binary;
using System.Text.RegularExpressions;

public static class PlotScalarCodec
{
    public static int Size(PlotScalar type) => type switch
    {
        PlotScalar.Int8 or PlotScalar.UInt8 or PlotScalar.Bool => 1,
        PlotScalar.Int16 or PlotScalar.UInt16 => 2,
        PlotScalar.Int32 or PlotScalar.UInt32 or PlotScalar.Float32 => 4,
        PlotScalar.Float64 => 8,
        _ => throw new ArgumentException("请选择明确的标量类型。")
    };

    public static PlotScalar Infer(string cType)
    {
        var type = Regex.Replace(cType, @"\b(const|volatile|restrict)\b", "").Trim();
        type = Regex.Replace(type, @"\s+", " ");
        return type switch
        {
            "int8_t" or "signed char" => PlotScalar.Int8,
            "uint8_t" or "unsigned char" => PlotScalar.UInt8,
            "int16_t" or "short" or "short int" or "signed short" => PlotScalar.Int16,
            "uint16_t" or "unsigned short" or "short unsigned int" or "unsigned short int" => PlotScalar.UInt16,
            "int32_t" or "int" or "signed int" or "long" or "long int" => PlotScalar.Int32,
            "uint32_t" or "unsigned" or "unsigned int" or "unsigned long" or "long unsigned int" or "unsigned long int" => PlotScalar.UInt32,
            "float" => PlotScalar.Float32,
            "double" => PlotScalar.Float64,
            "bool" or "_Bool" => PlotScalar.Bool,
            _ => throw new ArgumentException("不能自动判断类型 “" + cType + "”，请明确选择数值类型（数组和结构体请指定元素/成员）。")
        };
    }

    public static double Decode(ReadOnlySpan<byte> data, PlotScalar type, bool littleEndian)
    {
        if (data.Length != Size(type))
        {
            throw new ArgumentException("采样字节数与类型不匹配。");
        }
        Span<byte> bytes = stackalloc byte[data.Length];
        data.CopyTo(bytes);
        if (!littleEndian)
        {
            bytes.Reverse();
        }
        return type switch
        {
            PlotScalar.Int8 => (sbyte)bytes[0],
            PlotScalar.UInt8 => bytes[0],
            PlotScalar.Bool => bytes[0] == 0 ? 0 : 1,
            PlotScalar.Int16 => BinaryPrimitives.ReadInt16LittleEndian(bytes),
            PlotScalar.UInt16 => BinaryPrimitives.ReadUInt16LittleEndian(bytes),
            PlotScalar.Int32 => BinaryPrimitives.ReadInt32LittleEndian(bytes),
            PlotScalar.UInt32 => BinaryPrimitives.ReadUInt32LittleEndian(bytes),
            PlotScalar.Float32 => BinaryPrimitives.ReadSingleLittleEndian(bytes),
            PlotScalar.Float64 => BinaryPrimitives.ReadDoubleLittleEndian(bytes),
            _ => throw new ArgumentOutOfRangeException(nameof(type))
        };
    }
}
