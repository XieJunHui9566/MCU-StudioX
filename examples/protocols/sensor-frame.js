// StudioX 串口脚本 API v1。保存后在协议页选择本文件，再点击“应用 / 重载”。
// 示例协议：AA 55 | 数据长度 N (1..64) | N 个数据字节 | XOR 校验
// XOR = 长度字节与全部数据字节异或。第一个数据字节为命令。
// 完整示例：AA 55 03 01 09 C4 CF（命令 1，温度 25.00 °C）。
// bytes 包含尚未消费的字节；半帧返回 consumed=0，宿主会拼接下一批。
// context: apiVersion、direction (RX/TX)、timestamp (ISO UTC)、clock、flush。
function decode(bytes, context) {
    const frames = [];
    let offset = 0;
    while (offset < bytes.length) {
        const start = offset;
        if (bytes[offset] !== 0xAA) { offset++; continue; }
        if (bytes.length - offset < 3) break;
        if (bytes[offset + 1] !== 0x55) { offset++; continue; }
        const size = bytes[offset + 2];
        if (size < 1 || size > 64) { offset++; continue; }
        const length = size + 4;
        if (bytes.length - offset < length) break;
        let checksum = size;
        for (let i = 0; i < size; i++) checksum ^= bytes[offset + 3 + i];
        const good = checksum === bytes[offset + length - 1];
        const fields = {
            "方向": context.direction,
            "命令": "0x" + bytes[offset + 3].toString(16).padStart(2, "0"),
            "数据长度": String(size),
            "校验": good ? "XOR 正确" : "XOR 错误"
        };
        if (size === 3 && bytes[offset + 3] === 1) {
            let value = (bytes[offset + 4] << 8) | bytes[offset + 5];
            if (value >= 32768) value -= 65536;
            fields["温度"] = (value / 100).toFixed(2) + " °C";
        }
        frames.push({ offset: start, length, summary: "传感器 · " + (fields["温度"] || fields["命令"]),
            status: good ? "ok" : "error", fields });
        offset += length;
    }
    return { consumed: offset, frames };
}
