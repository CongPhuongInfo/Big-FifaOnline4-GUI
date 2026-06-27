/*
 * ChunkZipExplorer.cs — WinForms GUI cho EA BIG (ViV4 + chunkzip)
 * .NET Framework 4.x | C# 5 Compatible
 *
 * Cấu trúc file .big (chunkzip):
 *   [0x00-0x3F]  64 bytes header (hash/signature, placeholder)
 *   [0x40+]      chunkzip block 0: TOC (ViV4) nén deflate
 *   [0x40 align] chunkzip block 1..N: dữ liệu từng file
 *
 * Tính năng GUI:
 *  - Mở file .big, hiển thị TreeView từng entry (tên, kích thước, ID)
 *  - Xem hex dump nội dung file
 *  - Thêm file mới vào archive
 *  - Thay thế (edit) file bằng file ngoài
 *  - Xóa file khỏi archive
 *  - Xuất (extract) file ra ngoài
 *  - Lưu lại file .big (rebuild TOC + chunkzip + align)
 */

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows.Forms;

namespace ChunkZipExplorer
{
    // ─── Model ───────────────────────────────────────────────────────────────────
    class ArchiveEntry
    {
        public int    Index;
        public uint   ArchiveOffset;       // offset trong không gian giải nén
        public uint   UncompressedSize;
        public byte[] Id = new byte[12];   // 12-byte binary ID
        public string Name;
        public byte[] Data;                // dữ liệu giải nén
        public bool   Modified;
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  MAIN FORM
    // ════════════════════════════════════════════════════════════════════════════
    class MainForm : Form
    {
        // ── Controls ──────────────────────────────────────────────────────────
        TreeView    tvFiles;
        ListView    lvInfo;
        RichTextBox rtbHex;
        StatusStrip statusStrip;
        ToolStripStatusLabel lblStatus;
        MenuStrip   menuStrip;

        // ── State ─────────────────────────────────────────────────────────────
        string              currentFile = null;
        List<ArchiveEntry>  entries     = new List<ArchiveEntry>();
        bool                dirty       = false;

        // ── Constants ─────────────────────────────────────────────────────────
        const int SIGNATURE_SIZE     = 64;
        const int ALIGN              = 0x40;
        const int DEFAULT_CHUNK_SIZE = 184320;

        // ════════════════════════════════════════════════════════════════════════
        public MainForm()
        {
            Text          = "ChunkZip Explorer – EA BIG (ViV4) Tool";
            Size          = new Size(1050, 680);
            MinimumSize   = new Size(750, 500);
            StartPosition = FormStartPosition.CenterScreen;
            Font          = new Font("Segoe UI", 9f);
            BuildUI();
        }

        // ════════════════════════════════════════════════════════════════════════
        //  BUILD UI
        // ════════════════════════════════════════════════════════════════════════
        void BuildUI()
        {
            // ── MenuStrip ─────────────────────────────────────────────────────
            menuStrip      = new MenuStrip();
            menuStrip.Dock = DockStyle.Top;

            var miFile   = new ToolStripMenuItem("&File");
            var miOpen   = new ToolStripMenuItem("&Mở file .big...", null, OnOpen,    Keys.Control | Keys.O);
            var miSave   = new ToolStripMenuItem("&Lưu",             null, OnSave,    Keys.Control | Keys.S);
            var miSaveAs = new ToolStripMenuItem("Lưu &As...",       null, OnSaveAs);
            var miClose  = new ToolStripMenuItem("&Đóng file",       null, OnClose);
            var miExit   = new ToolStripMenuItem("Thoá&t",           null, (s,e) => Close());
            miFile.DropDownItems.AddRange(new ToolStripItem[]{
                miOpen, miSave, miSaveAs, new ToolStripSeparator(), miClose,
                new ToolStripSeparator(), miExit
            });

            var miEntry   = new ToolStripMenuItem("&Entry");
            var miAdd     = new ToolStripMenuItem("&Thêm file mới...",     null, OnAdd,     Keys.Insert);
            var miReplace = new ToolStripMenuItem("&Thay thế entry...",    null, OnReplace, Keys.Control | Keys.R);
            var miDelete  = new ToolStripMenuItem("&Xóa entry",            null, OnDelete,  Keys.Delete);
            var miExtract = new ToolStripMenuItem("&Xuất entry ra file...",null, OnExtract, Keys.Control | Keys.E);
            var miExtractAll = new ToolStripMenuItem("Xuất &tất cả...",    null, OnExtractAll);
            miEntry.DropDownItems.AddRange(new ToolStripItem[]{
                miAdd, miReplace, miDelete,
                new ToolStripSeparator(), miExtract, miExtractAll
            });

            menuStrip.Items.Add(miFile);
            menuStrip.Items.Add(miEntry);

            // ── Toolbar ───────────────────────────────────────────────────────
            var toolbar  = new ToolStrip();
            toolbar.Dock = DockStyle.Top;

            Append(toolbar, "📂 Mở",        OnOpen);
            Append(toolbar, "💾 Lưu",        OnSave);
            toolbar.Items.Add(new ToolStripSeparator());
            Append(toolbar, "➕ Thêm",       OnAdd);
            Append(toolbar, "✏ Thay thế",   OnReplace);
            Append(toolbar, "🗑 Xóa",        OnDelete);
            toolbar.Items.Add(new ToolStripSeparator());
            Append(toolbar, "📤 Xuất",       OnExtract);
            Append(toolbar, "📦 Xuất tất cả",OnExtractAll);

            // ── Left panel: TreeView ──────────────────────────────────────────
            var panelLeft  = new Panel();
            panelLeft.Dock  = DockStyle.Left;
            panelLeft.Width = 260;
            panelLeft.BorderStyle = BorderStyle.FixedSingle;

            var lblTree       = MakeHeader("  Danh sách Entry (ViV4)");
            lblTree.Dock      = DockStyle.Top;

            tvFiles           = new TreeView();
            tvFiles.Dock      = DockStyle.Fill;
            tvFiles.HideSelection = false;
            tvFiles.BorderStyle   = BorderStyle.None;
            tvFiles.AfterSelect  += OnTreeSelect;

            var ctx = new ContextMenuStrip();
            ctx.Items.Add("Thêm file mới...",      null, OnAdd);
            ctx.Items.Add("Thay thế entry...",      null, OnReplace);
            ctx.Items.Add("Xóa entry",              null, OnDelete);
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add("Xuất entry ra file...",  null, OnExtract);
            ctx.Items.Add("Xuất tất cả...",         null, OnExtractAll);
            tvFiles.ContextMenuStrip = ctx;

            panelLeft.Controls.Add(tvFiles);
            panelLeft.Controls.Add(lblTree);

            // ── Splitter ─────────────────────────────────────────────────────
            var splitter  = new Splitter();
            splitter.Dock = DockStyle.Left;
            splitter.Width = 4;

            // ── Right panel ───────────────────────────────────────────────────
            var panelRight  = new Panel();
            panelRight.Dock = DockStyle.Fill;

            // ListView thông tin
            lvInfo             = new ListView();
            lvInfo.Dock        = DockStyle.Top;
            lvInfo.Height      = 145;
            lvInfo.View        = View.Details;
            lvInfo.FullRowSelect = true;
            lvInfo.GridLines   = true;
            lvInfo.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lvInfo.Columns.Add("Thuộc tính", 170);
            lvInfo.Columns.Add("Giá trị",    380);
            lvInfo.BorderStyle = BorderStyle.FixedSingle;

            var splitterH  = new Splitter();
            splitterH.Dock = DockStyle.Top;
            splitterH.Height = 4;

            var lblHex       = MakeHeader("  Hex Preview (512 byte đầu)");
            lblHex.Dock      = DockStyle.Top;

            rtbHex           = new RichTextBox();
            rtbHex.Dock      = DockStyle.Fill;
            rtbHex.ReadOnly  = true;
            rtbHex.Font      = new Font("Courier New", 8.5f);
            rtbHex.BackColor = Color.FromArgb(25, 25, 35);
            rtbHex.ForeColor = Color.FromArgb(100, 220, 130);
            rtbHex.BorderStyle = BorderStyle.None;
            rtbHex.WordWrap  = false;

            panelRight.Controls.Add(rtbHex);
            panelRight.Controls.Add(lblHex);
            panelRight.Controls.Add(splitterH);
            panelRight.Controls.Add(lvInfo);

            // ── StatusStrip ───────────────────────────────────────────────────
            statusStrip      = new StatusStrip();
            lblStatus        = new ToolStripStatusLabel("Sẵn sàng. Mở file .big để bắt đầu.");
            lblStatus.Spring = true;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            statusStrip.Items.Add(lblStatus);

            // ── Assemble ─────────────────────────────────────────────────────
            Controls.Add(panelRight);
            Controls.Add(splitter);
            Controls.Add(panelLeft);
            Controls.Add(toolbar);
            Controls.Add(menuStrip);
            Controls.Add(statusStrip);
            MainMenuStrip = menuStrip;
        }

        static Label MakeHeader(string text)
        {
            var lbl       = new Label();
            lbl.Text      = text;
            lbl.Height    = 22;
            lbl.BackColor = Color.FromArgb(45, 55, 85);
            lbl.ForeColor = Color.White;
            lbl.Font      = new Font("Segoe UI", 9f, FontStyle.Bold);
            lbl.TextAlign = ContentAlignment.MiddleLeft;
            return lbl;
        }

        static void Append(ToolStrip ts, string label, EventHandler h)
        {
            var btn   = new ToolStripButton(label);
            btn.Click += h;
            ts.Items.Add(btn);
        }

        // ════════════════════════════════════════════════════════════════════════
        //  FILE OPERATIONS
        // ════════════════════════════════════════════════════════════════════════
        void OnOpen(object sender, EventArgs e)
        {
            if (dirty && !ConfirmDiscard()) return;
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title  = "Mở file EA BIG (.big)";
                dlg.Filter = "EA BIG files (*.big)|*.big|All files (*.*)|*.*";
                if (dlg.ShowDialog() != DialogResult.OK) return;
                LoadFile(dlg.FileName);
            }
        }

        void LoadFile(string path)
        {
            try
            {
                byte[] raw = File.ReadAllBytes(path);

                // Quét tất cả chunkzip block
                List<int> blockStarts = ScanChunkBlocks(raw);
                if (blockStarts.Count == 0)
                    throw new Exception("Không tìm thấy block chunkzip nào. File không đúng định dạng ViV4.");

                // Block 0 → TOC
                int tocEnd;
                byte[] tocData = CzDecompress(raw, blockStarts[0], out tocEnd);
                List<ArchiveEntry> parsed = ParseToc(tocData);

                // Block 1..N → file data (ánh xạ theo thứ tự offset tăng dần)
                parsed.Sort((a, b) => a.ArchiveOffset.CompareTo(b.ArchiveOffset));

                int dataCount = blockStarts.Count - 1;
                for (int i = 0; i < parsed.Count && i < dataCount; i++)
                {
                    int dummy;
                    try
                    {
                        parsed[i].Data = CzDecompress(raw, blockStarts[i + 1], out dummy);
                    }
                    catch
                    {
                        parsed[i].Data = new byte[0];
                    }
                    parsed[i].Index = i;

                    // Auto-detect extension nếu tên chưa có
                    string ext = DetectExtension(parsed[i].Data);
                    string hexId = BitConverter.ToString(parsed[i].Id).Replace("-", "").ToLower();
                    parsed[i].Name = string.Format("file_{0:D4}_{1}{2}", i, hexId, ext);
                }

                currentFile = path;
                entries     = parsed;
                dirty       = false;
                RefreshTree();
                Text = string.Format("ChunkZip Explorer – {0}", System.IO.Path.GetFileName(path));
                SetStatus(string.Format("Đã tải {0} entry từ {1} chunkzip block.",
                                        entries.Count, blockStarts.Count));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi tải file:\n" + ex.Message, "Lỗi",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void OnSave(object sender, EventArgs e)
        {
            if (currentFile == null) { OnSaveAs(sender, e); return; }
            SaveFile(currentFile);
        }

        void OnSaveAs(object sender, EventArgs e)
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title  = "Lưu file .big";
                dlg.Filter = "EA BIG files (*.big)|*.big|All files (*.*)|*.*";
                if (currentFile != null)
                    dlg.FileName = System.IO.Path.GetFileName(currentFile);
                if (dlg.ShowDialog() != DialogResult.OK) return;
                SaveFile(dlg.FileName);
                currentFile = dlg.FileName;
                Text = "ChunkZip Explorer – " + System.IO.Path.GetFileName(currentFile);
            }
        }

        void SaveFile(string path)
        {
            if (entries.Count == 0)
            {
                MessageBox.Show("Không có entry nào để lưu.", "Thông báo",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                // Tính lại archive offset và TOC
                int tocSize = 16 + 20 * entries.Count;
                uint currentOffset = (uint)tocSize;
                for (int i = 0; i < entries.Count; i++)
                {
                    entries[i].ArchiveOffset    = currentOffset;
                    entries[i].UncompressedSize = (uint)entries[i].Data.Length;
                    currentOffset += (uint)entries[i].Data.Length;
                }
                uint totalSize = currentOffset;

                byte[] tocBytes = BuildToc(totalSize);
                byte[] tocBlock = CzCompress(tocBytes);

                using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write))
                using (var bw = new BinaryWriter(fs))
                {
                    // 64 bytes signature placeholder
                    bw.Write(new byte[SIGNATURE_SIZE]);

                    // TOC block
                    bw.Write(tocBlock);
                    PadToAlign(bw, ALIGN);

                    // Data blocks
                    foreach (ArchiveEntry ent in entries)
                    {
                        byte[] block = CzCompress(ent.Data);
                        bw.Write(block);
                        PadToAlign(bw, ALIGN);
                    }
                }

                dirty = false;
                foreach (ArchiveEntry ent in entries) ent.Modified = false;
                RefreshTree();
                SetStatus(string.Format("Đã lưu: {0}  ({1} entry)", path, entries.Count));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi lưu file:\n" + ex.Message, "Lỗi",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void OnClose(object sender, EventArgs e)
        {
            if (dirty && !ConfirmDiscard()) return;
            currentFile = null;
            entries.Clear();
            dirty = false;
            tvFiles.Nodes.Clear();
            lvInfo.Items.Clear();
            rtbHex.Clear();
            Text = "ChunkZip Explorer – EA BIG (ViV4) Tool";
            SetStatus("Đã đóng file.");
        }

        // ════════════════════════════════════════════════════════════════════════
        //  ENTRY OPERATIONS
        // ════════════════════════════════════════════════════════════════════════
        void OnAdd(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title     = "Chọn file để thêm vào archive";
                dlg.Filter    = "All files (*.*)|*.*";
                dlg.Multiselect = true;
                if (dlg.ShowDialog() != DialogResult.OK) return;

                foreach (string f in dlg.FileNames)
                {
                    byte[] data = File.ReadAllBytes(f);
                    string fn   = System.IO.Path.GetFileName(f);
                    var ent     = new ArchiveEntry
                    {
                        Index            = entries.Count,
                        ArchiveOffset    = 0,
                        UncompressedSize = (uint)data.Length,
                        Id               = new byte[12],
                        Name             = fn,
                        Data             = data,
                        Modified         = true
                    };
                    // Ghi index vào 4 byte cuối Id
                    int idx = entries.Count;
                    ent.Id[8] = (byte)(idx >> 24); ent.Id[9]  = (byte)(idx >> 16);
                    ent.Id[10]= (byte)(idx >> 8);  ent.Id[11] = (byte)idx;
                    entries.Add(ent);
                }
                MarkDirty();
                RefreshTree();
                SetStatus(string.Format("Đã thêm {0} file mới.", dlg.FileNames.Length));
            }
        }

        void OnReplace(object sender, EventArgs e)
        {
            ArchiveEntry ent = GetSelected();
            if (ent == null) { NeedSelection(); return; }

            using (var dlg = new OpenFileDialog())
            {
                dlg.Title  = "Thay thế \"" + ent.Name + "\" bằng file:";
                dlg.Filter = "All files (*.*)|*.*";
                if (dlg.ShowDialog() != DialogResult.OK) return;

                ent.Data             = File.ReadAllBytes(dlg.FileName);
                ent.UncompressedSize = (uint)ent.Data.Length;
                ent.Modified         = true;
                MarkDirty();
                RefreshTree();
                ShowEntryInfo(ent);
                SetStatus(string.Format("Đã thay thế [{0}] bằng: {1}",
                          ent.Name, System.IO.Path.GetFileName(dlg.FileName)));
            }
        }

        void OnDelete(object sender, EventArgs e)
        {
            ArchiveEntry ent = GetSelected();
            if (ent == null) { NeedSelection(); return; }

            var res = MessageBox.Show(
                string.Format("Xóa entry [{0}] ({1:N0} bytes)?", ent.Name, ent.Data.Length),
                "Xác nhận xóa", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (res != DialogResult.Yes) return;

            int idx = ent.Index;
            entries.RemoveAt(idx);
            for (int i = 0; i < entries.Count; i++) entries[i].Index = i;
            MarkDirty();
            RefreshTree();
            lvInfo.Items.Clear();
            rtbHex.Clear();

            if (tvFiles.Nodes.Count > 0)
            {
                TreeNode root = tvFiles.Nodes[0];
                if (root.Nodes.Count > 0)
                {
                    int next = Math.Min(idx, root.Nodes.Count - 1);
                    tvFiles.SelectedNode = root.Nodes[next];
                }
            }
            SetStatus(string.Format("Đã xóa [{0}]. Còn {1} entry.", ent.Name, entries.Count));
        }

        void OnExtract(object sender, EventArgs e)
        {
            ArchiveEntry ent = GetSelected();
            if (ent == null) { NeedSelection(); return; }

            using (var dlg = new SaveFileDialog())
            {
                dlg.Title    = "Xuất entry ra file";
                dlg.FileName = ent.Name;
                dlg.Filter   = "All files (*.*)|*.*";
                if (dlg.ShowDialog() != DialogResult.OK) return;

                File.WriteAllBytes(dlg.FileName, ent.Data);
                SetStatus(string.Format("Đã xuất [{0}] → {1}  ({2:N0} bytes)",
                          ent.Name, System.IO.Path.GetFileName(dlg.FileName), ent.Data.Length));
            }
        }

        void OnExtractAll(object sender, EventArgs e)
        {
            if (entries.Count == 0)
            {
                MessageBox.Show("Không có entry nào.", "Thông báo",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Chọn thư mục xuất tất cả entry";
                if (dlg.ShowDialog() != DialogResult.OK) return;

                int count = 0;
                foreach (ArchiveEntry ent in entries)
                {
                    string outPath = System.IO.Path.Combine(dlg.SelectedPath, ent.Name);
                    File.WriteAllBytes(outPath, ent.Data);
                    count++;
                }
                SetStatus(string.Format("Đã xuất {0} entry vào: {1}", count, dlg.SelectedPath));
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        //  TREEVIEW  &  INFO PANEL
        // ════════════════════════════════════════════════════════════════════════
        void RefreshTree()
        {
            string selTag = tvFiles.SelectedNode != null
                            ? tvFiles.SelectedNode.Tag as string : null;

            tvFiles.BeginUpdate();
            tvFiles.Nodes.Clear();

            string rootLabel = currentFile != null
                ? System.IO.Path.GetFileName(currentFile) + (dirty ? "  *" : "")
                : "(Chưa mở file)";
            var root = new TreeNode(rootLabel);
            root.NodeFont = new Font(tvFiles.Font, FontStyle.Bold);
            root.Tag      = "__root__";

            foreach (ArchiveEntry ent in entries)
            {
                string label = string.Format("[{0:D4}]  {1}  [{2:N0} B]",
                                             ent.Index, ent.Name, ent.Data.Length);
                if (ent.Modified) label += "  ✎";
                var node    = new TreeNode(label);
                node.Tag    = ent.Index.ToString();
                node.ForeColor = ent.Modified ? Color.DodgerBlue : Color.Empty;
                root.Nodes.Add(node);
            }

            tvFiles.Nodes.Add(root);
            root.Expand();

            if (selTag != null && selTag != "__root__")
            {
                foreach (TreeNode n in root.Nodes)
                    if (n.Tag as string == selTag)
                    { tvFiles.SelectedNode = n; break; }
            }
            tvFiles.EndUpdate();
        }

        void OnTreeSelect(object sender, TreeViewEventArgs e)
        {
            ArchiveEntry ent = GetSelected();
            if (ent == null) { lvInfo.Items.Clear(); rtbHex.Clear(); return; }
            ShowEntryInfo(ent);
        }

        void ShowEntryInfo(ArchiveEntry ent)
        {
            lvInfo.Items.Clear();
            AddInfo("Index",          ent.Index.ToString());
            AddInfo("Tên file",       ent.Name);
            AddInfo("Kích thước",     string.Format("{0:N0} bytes", ent.Data.Length));
            AddInfo("Archive Offset", string.Format("0x{0:X8}  ({0:N0})", ent.ArchiveOffset));
            AddInfo("ID (hex)",       BitConverter.ToString(ent.Id).Replace("-", " ").ToLower());
            AddInfo("Trạng thái",     ent.Modified ? "Đã sửa đổi ✎" : "Gốc");
            AddInfo("Magic / Type",   DetectExtension(ent.Data));

            rtbHex.Text = BuildHexDump(ent.Data, Math.Min(512, ent.Data.Length));
        }

        void AddInfo(string k, string v)
        {
            var item = new ListViewItem(k);
            item.SubItems.Add(v);
            lvInfo.Items.Add(item);
        }

        string BuildHexDump(byte[] data, int length)
        {
            var sb = new StringBuilder();
            sb.AppendLine("  Offset    00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F   ASCII");
            sb.AppendLine("  " + new string('─', 74));
            int rows = (length + 15) / 16;
            for (int r = 0; r < rows; r++)
            {
                int rowStart = r * 16;
                sb.Append(string.Format("  {0:X8}  ", rowStart));
                var ascii = new StringBuilder();
                for (int c = 0; c < 16; c++)
                {
                    if (c == 8) sb.Append(" ");
                    int idx = rowStart + c;
                    if (idx < length)
                    {
                        byte b = data[idx];
                        sb.Append(string.Format("{0:X2} ", b));
                        ascii.Append(b >= 32 && b < 127 ? (char)b : '.');
                    }
                    else { sb.Append("   "); ascii.Append(' '); }
                }
                sb.Append("  ");
                sb.AppendLine(ascii.ToString());
            }
            if (data.Length > length)
                sb.AppendLine(string.Format("\n  ... (còn {0:N0} bytes)", data.Length - length));
            return sb.ToString();
        }

        // ════════════════════════════════════════════════════════════════════════
        //  CHUNKZIP CORE
        // ════════════════════════════════════════════════════════════════════════
        static byte[] CzDecompress(byte[] raw, int start, out int endOffset)
        {
            int pos = start;

            // chunk_type (8 bytes)
            string chunkType = Encoding.ASCII.GetString(raw, pos, 8).TrimEnd('\0');
            pos += 8;
            if (chunkType != "chunkzip")
                throw new NotSupportedException("Unsupported chunk type: " + chunkType);

            uint dummy = ReadBE(raw, pos); pos += 4;
            uint fullsize;
            int  blockType;
            if (dummy <= 2)
            {
                fullsize  = ReadBE(raw, pos); pos += 4;
                blockType = 0;
            }
            else
            {
                fullsize  = dummy;
                blockType = 1;
            }

            uint chunkSize = ReadBE(raw, pos); pos += 4;
            uint numChunks;
            if (blockType == 0)
            {
                numChunks = ReadBE(raw, pos); pos += 4;
                pos += 16; // 4 dummy longs
            }
            else
            {
                numChunks = (fullsize + chunkSize - 1) / chunkSize;
            }

            using (var output = new MemoryStream())
            {
                for (uint c = 0; c < numChunks; c++)
                {
                    uint zsize = ReadBE(raw, pos); pos += 4;
                    bool compressed = true;
                    if (blockType == 0)
                    {
                        uint flag = ReadBE(raw, pos); pos += 4;
                        if (flag == 4) compressed = false;
                    }

                    if (compressed)
                    {
                        using (var ms = new MemoryStream(raw, pos, (int)zsize))
                        using (var ds = new DeflateStream(ms, CompressionMode.Decompress))
                        using (var tmp = new MemoryStream())
                        {
                            ds.CopyTo(tmp);
                            byte[] dec = tmp.ToArray();
                            output.Write(dec, 0, dec.Length);
                        }
                    }
                    else
                    {
                        output.Write(raw, pos, (int)zsize);
                    }
                    pos += (int)zsize;
                }
                endOffset = pos;
                return output.ToArray();
            }
        }

        static byte[] CzCompress(byte[] data, int chunkSize = DEFAULT_CHUNK_SIZE)
        {
            int numChunks = Math.Max(1, (data.Length + chunkSize - 1) / chunkSize);
            var chunks    = new List<byte[]>();
            for (int i = 0; i < numChunks; i++)
            {
                int srcOff = i * chunkSize;
                int srcLen = Math.Min(chunkSize, data.Length - srcOff);
                using (var ms = new MemoryStream())
                using (var ds = new DeflateStream(ms, CompressionLevel.Optimal))
                {
                    ds.Write(data, srcOff, srcLen);
                    ds.Close();
                    chunks.Add(ms.ToArray());
                }
            }

            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(Encoding.ASCII.GetBytes("chunkzip"));
                WriteBE(bw, 2);                      // dummy=2 → TYPE 0
                WriteBE(bw, (uint)data.Length);      // fullsize
                WriteBE(bw, (uint)chunkSize);         // chunk_size
                WriteBE(bw, (uint)numChunks);         // num_chunks
                WriteBE(bw, 16); bw.Write(new byte[12]); // 4 dummy longs

                foreach (byte[] ch in chunks)
                {
                    WriteBE(bw, (uint)ch.Length); // zsize
                    WriteBE(bw, 1u);              // flag: compressed
                    bw.Write(ch);
                }
                return ms.ToArray();
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        //  ViV4 TOC
        // ════════════════════════════════════════════════════════════════════════
        static List<ArchiveEntry> ParseToc(byte[] toc)
        {
            if (toc[0] != 'V' || toc[1] != 'i' || toc[2] != 'V' || toc[3] != '4')
                throw new Exception("TOC magic không hợp lệ: " +
                                    Encoding.ASCII.GetString(toc, 0, 4));

            uint numFiles = ReadBE(toc, 8);
            var  result   = new List<ArchiveEntry>();
            int  pos      = 16;
            for (uint i = 0; i < numFiles; i++)
            {
                if (pos + 20 > toc.Length) break;
                var ent = new ArchiveEntry
                {
                    ArchiveOffset    = ReadBE(toc, pos),
                    UncompressedSize = ReadBE(toc, pos + 4),
                    Id               = new byte[12]
                };
                Buffer.BlockCopy(toc, pos + 8, ent.Id, 0, 12);
                result.Add(ent);
                pos += 20;
            }
            return result;
        }

        byte[] BuildToc(uint totalSize)
        {
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(new byte[]{ (byte)'V',(byte)'i',(byte)'V',(byte)'4' });
                bw.Write(totalSize);                              // LE archive_size
                WriteBE(bw, (uint)entries.Count);                 // BE num_files
                WriteBE(bw, (uint)(16 + 20 * entries.Count));    // BE first_offset

                foreach (ArchiveEntry ent in entries)
                {
                    WriteBE(bw, ent.ArchiveOffset);
                    WriteBE(bw, ent.UncompressedSize);
                    bw.Write(ent.Id, 0, 12);
                }
                return ms.ToArray();
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        //  SCAN CHUNK BLOCKS
        // ════════════════════════════════════════════════════════════════════════
        static List<int> ScanChunkBlocks(byte[] raw)
        {
            var result = new List<int>();
            int pos    = SIGNATURE_SIZE;

            while (pos < raw.Length - 8)
            {
                if (raw[pos]=='c' && raw[pos+1]=='h' && raw[pos+2]=='u' &&
                    raw[pos+3]=='n' && raw[pos+4]=='k')
                {
                    try
                    {
                        int endPos;
                        CzDecompress(raw, pos, out endPos);
                        result.Add(pos);
                        pos = AlignUp(endPos, ALIGN);
                        continue;
                    }
                    catch { }
                }
                pos++;
            }
            return result;
        }

        // ════════════════════════════════════════════════════════════════════════
        //  HELPERS
        // ════════════════════════════════════════════════════════════════════════
        ArchiveEntry GetSelected()
        {
            TreeNode n = tvFiles.SelectedNode;
            if (n == null || n.Tag == null || n.Tag.ToString() == "__root__") return null;
            int idx;
            if (!int.TryParse(n.Tag.ToString(), out idx)) return null;
            if (idx < 0 || idx >= entries.Count) return null;
            return entries[idx];
        }

        void NeedSelection()
        {
            MessageBox.Show("Hãy chọn một entry trong danh sách.", "Thông báo",
                            MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        void MarkDirty() { dirty = true; }

        void SetStatus(string msg) { lblStatus.Text = msg; }

        bool ConfirmDiscard()
        {
            return MessageBox.Show(
                "File đã có thay đổi chưa lưu. Tiếp tục sẽ mất dữ liệu. Tiếp tục?",
                "Chưa lưu", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }

        static string DetectExtension(byte[] d)
        {
            if (d == null || d.Length < 4) return ".bin";
            if (d[0]=='V' && d[1]=='i' && d[2]=='V' && d[3]=='4') return ".big";
            if (d[0]=='B' && d[1]=='I' && d[2]=='G')               return ".big";
            if (d[0]==0x89 && d[1]=='P' && d[2]=='N' && d[3]=='G') return ".png";
            if (d[0]==0xFF && d[1]==0xD8)                            return ".jpg";
            if (d[0]=='D' && d[1]=='D' && d[2]=='S' && d[3]==' ')  return ".dds";
            if (d[0]=='R' && d[1]=='I' && d[2]=='F' && d[3]=='F')  return ".riff";
            if (d[0]=='<')                                           return ".xml";
            if (d[0]=='{')                                           return ".json";
            if (d[0]=='P' && d[1]=='K')                             return ".zip";
            if (d[0]==0x1F && d[1]==0x8B)                           return ".gz";
            return ".bin";
        }

        static uint ReadBE(byte[] b, int o)
        {
            return (uint)((b[o]<<24)|(b[o+1]<<16)|(b[o+2]<<8)|b[o+3]);
        }

        static void WriteBE(BinaryWriter bw, uint v)
        {
            bw.Write((byte)(v>>24)); bw.Write((byte)(v>>16));
            bw.Write((byte)(v>>8));  bw.Write((byte)v);
        }

        static int AlignUp(int v, int a) { return (v + a - 1) & ~(a - 1); }

        static void PadToAlign(BinaryWriter bw, int align)
        {
            long pos = bw.BaseStream.Position;
            long rem = pos % align;
            if (rem != 0) bw.Write(new byte[align - rem]);
        }
    }

    // ════════════════════════════════════════════════════════════════════════════
    //  ENTRY POINT
    // ════════════════════════════════════════════════════════════════════════════
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
