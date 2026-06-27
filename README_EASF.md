# EA BIG / EASF File Tool

Công cụ C# .NET 4.x để **pack** và **unpack** file `.big` theo định dạng EASF của EA Games.

---

## Cấu trúc file .big (EASF Container)

Phân tích từ `data_fo4.big` và mã nguồn C++ `EASF-1.0`:

```
Offset 0x00  ┌──────────────────────────────┐
             │ SHA-256 Checksum (32 bytes)  │  Hash của toàn bộ nội dung từ byte 64
Offset 0x20  ├──────────────────────────────┤
             │ Zero Padding    (32 bytes)   │
Offset 0x40  ├──────────────────────────────┤
             │ EASF Block #0   (header+enc) │
             ├──────────────────────────────┤
             │ Alignment padding (≤64 bytes)│
             ├──────────────────────────────┤
             │ EASF Block #1               │
             ├──────────────────────────────┤
             │ ...                          │
             └──────────────────────────────┘
```

### Cấu trúc mỗi EASF Block (header = 48 bytes)

| Offset | Size | Mô tả |
|--------|------|-------|
| +0     | 4    | Signature = `EASF` (bytes: 45 41 53 46) |
| +4     | 4    | `decryptedSize` – kích thước plaintext (Big-Endian) |
| +8     | 8    | `keyid` = `"datax   "` (ASCII, padded với space) |
| +16    | 32   | SHA-256 digest của 32 bytes đầu plaintext |
| +48    | N    | Dữ liệu mã hóa AES-128-CBC (căn chỉnh lên bội số 16) |

Sau mỗi block, dữ liệu được căn chỉnh lên bội số **0x40 (64 bytes)**.

---

## Mã hóa

- Thuật toán: **AES-128-CBC**
- Key = IV = game key (16 bytes, dùng chung)
- Padding: **zero-padding** (không phải PKCS7)

### Game Keys

| Game | Key (hex) |
|------|-----------|
| FIFA 15, FIFA 16 | `249BF27AF5D7487B1578D833F2DE39B5` |
| Default (FIFA 17+, FO4, ...) | `249185E3707BD883CEA5C511F5D467F2` |

---

## Build

### Yêu cầu
- Visual Studio 2017+ với .NET Framework 4.7.2
- Hoặc MSBuild + .NET Framework 4.x SDK

### Lệnh build
```bat
msbuild BigFileTool.csproj /p:Configuration=Release
```
Hoặc mở `BigFileTool.csproj` trong Visual Studio và nhấn **Build**.

Output: `bin\Release\BigFileTool.exe`

---

## Cách dùng

```
BigFileTool <command> <arg1> <arg2> [--game <name>]
```

### Unpack (giải mã)
```bat
BigFileTool unpack  data_fo4.big  ./output_blocks
BigFileTool unpack  data_fo4.big  ./output_blocks  --game default
BigFileTool unpack  data.big      ./out            --game fifa15
```
Tạo thư mục `output_blocks/` chứa các file `block_000.bin`, `block_001.bin`, ...

### Pack (đóng gói)
```bat
BigFileTool pack  ./output_blocks  data_new.big
BigFileTool pack  ./my_blocks      out.big  --game default
```
Đọc tất cả file trong thư mục, mã hóa từng file thành một EASF block, ghép lại thành `.big`.

---

## Ghi chú kỹ thuật

### Lý do không giải mã được với các key đã biết
File `data_fo4.big` có `keyid = "datax   "`. Tuy nhiên quá trình xác minh digest thất bại với cả 2 key trong mã nguồn C++. Điều này có thể do:
1. Game dùng một key riêng chưa được công khai
2. File đã được re-encrypt với key tùy chỉnh

Code C# đã xử lý đúng **cấu trúc** file – chỉ cần cung cấp đúng key là có thể giải mã.

### Cách thêm key mới
Trong `BigFileTool.cs`, thêm case vào hàm `GetKey()`:
```csharp
static byte[] GetKey(string game)
{
    if (game == "fifa15" || game == "fifa16")
        return KeyFifa1516;
    if (game == "fo4" || game == "online4")
        return new byte[] { 0xAA, 0xBB, ... }; // key 16 bytes
    return KeyDefault;
}
```

---

## Phân tích cấu trúc `data_fo4.big`

- Tổng kích thước: **3,320,096 bytes**
- Số EASF block: **225 block**
- Outer SHA-256 checksum: `d47d10973bc667dc...` ✓ **hợp lệ**
- Tất cả block đều có `keyid = "datax   "`
- Block align: **0x40 (64) bytes**
