/*
 * FlatChunkZipExplorer.cs — WinForms GUI cho EA BIG dạng "Flat ChunkZip"
 * .NET Framework 4.x | C# 5 Compatible
 *
 * Format: data_front_end.big, v.v.
 * ┌──────────────────────────────────────────────────┐
 * │ [0x00-0x1F]  32 bytes SHA-256 của toàn bộ body  │
 * │ [0x20-0x3F]  32 bytes zero padding               │
 * │ [0x40+]      Block 0: chunkzip (file 0)          │
 * │              Block 1: chunkzip (file 1)          │
 * │              ...  (căn lề 0x40)                  │
 * └──────────────────────────────────────────────────┘
 *
 * KHÁC với:
 *  - EASF: mỗi block có header EASF + AES-128-CBC
 *  - ViV4: có TOC nén riêng ở block 0
 * Ở đây: KHÔNG có TOC, KHÔNG có AES, KHÔNG có filename nội bộ
 * Mỗi chunkzip block = 1 file (raw deflate, không header zlib)
 */

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace FlatChunkZipExplorer
{
    // ─── Model ───────────────────────────────────────────────────────────────────
    class FlatEntry
    {
        public int    Index;
        public int    OffsetInFile;      // vị trí block trong file gốc
        public int    CompressedSize;    // tổng bytes nén (bao gồm cả chunkzip header)
        public byte[] Data;             // dữ liệu đã giải nén
        public string Name;             // tên file (gán thủ công, không có trong archive)
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
        Label       lblSha;

        // ── State ─────────────────────────────────────────────────────────────
        string             currentFile = null;
        List<FlatEntry>    entries     = new List<FlatEntry>();
        bool               dirty       = false;
        bool               shaOk       = false;

        // ── Constants ─────────────────────────────────────────────────────────
        const int HEADER_SIZE        = 64;   // 32 SHA + 32 zeros
        const int ALIGN              = 0x40;
        const int DEFAULT_CHUNK_SIZE = 184320;

        // ════════════════════════════════════════════════════════════════════════
        public MainForm()
        {
            Text          = "Flat ChunkZip Explorer – EA BIG Tool";
            Size          = new Size(1060, 690);
            MinimumSize   = new Size(760, 500);
            StartPosition = FormStartPosition.CenterScreen;
            Font          = new Font("Segoe UI", 9f);
            BuildUI();
        }

        // ════════════════════════════════════════════════════════════════════════
        //  BUILD UI
        // ════════════════════════════════════════════════════════════════════════
        void BuildUI()
        {
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
                new ToolStripSeparator(), miExit });

            var miEntry      = new ToolStripMenuItem("&Entry");
            var miAdd        = new ToolStripMenuItem("&Thêm file mới...",      null, OnAdd,        Keys.Insert);
            var miReplace    = new ToolStripMenuItem("&Thay thế entry...",     null, OnReplace,    Keys.Control | Keys.R);
            var miDelete     = new ToolStripMenuItem("&Xóa entry",             null, OnDelete,     Keys.Delete);
            var miMoveUp     = new ToolStripMenuItem("Di chuyển &lên",         null, OnMoveUp,     Keys.Control | Keys.Up);
            var miMoveDown   = new ToolStripMenuItem("Di chuyển &xuống",       null, OnMoveDown,   Keys.Control | Keys.Down);
            var miExtract    = new ToolStripMenuItem("&Xuất entry ra file...", null, OnExtract,    Keys.Control | Keys.E);
            var miExtractAll = new ToolStripMenuItem("Xuất &tất cả...",        null, OnExtractAll);
            var miRename     = new ToolStripMenuItem("Đổi tê&n entry...",      null, OnRename,     Keys.F2);
            miEntry.DropDownItems.AddRange(new ToolStripItem[]{
                miAdd, miReplace, miDelete,
                new ToolStripSeparator(), miMoveUp, miMoveDown,
                new ToolStripSeparator(), miExtract, miExtractAll,
                new ToolStripSeparator(), miRename });

            menuStrip.Items.Add(miFile);
            menuStrip.Items.Add(miEntry);

            // Toolbar
            var toolbar  = new ToolStrip();
            toolbar.Dock = DockStyle.Top;
            Btn(toolbar, "📂 Mở",         OnOpen);
            Btn(toolbar, "💾 Lưu",         OnSave);
            toolbar.Items.Add(new ToolStripSeparator());
            Btn(toolbar, "➕ Thêm",        OnAdd);
            Btn(toolbar, "✏ Thay thế",    OnReplace);
            Btn(toolbar, "🗑 Xóa",         OnDelete);
            toolbar.Items.Add(new ToolStripSeparator());
            Btn(toolbar, "⬆ Lên",          OnMoveUp);
            Btn(toolbar, "⬇ Xuống",        OnMoveDown);
            toolbar.Items.Add(new ToolStripSeparator());
            Btn(toolbar, "📤 Xuất",        OnExtract);
            Btn(toolbar, "📦 Xuất tất cả", OnExtractAll);

            // SHA-256 info bar
            lblSha           = new Label();
            lblSha.Dock      = DockStyle.Top;
            lblSha.Height    = 22;
            lblSha.BackColor = Color.FromArgb(30, 40, 30);
            lblSha.ForeColor = Color.LightGreen;
            lblSha.Font      = new Font("Courier New", 8f);
            lblSha.TextAlign = ContentAlignment.MiddleLeft;
            lblSha.Text      = "  SHA-256: (chưa mở file)";

            // Left panel
            var panelLeft       = new Panel();
            panelLeft.Dock      = DockStyle.Left;
            panelLeft.Width     = 270;
            panelLeft.BorderStyle = BorderStyle.FixedSingle;

            var lblTree         = MkHeader("  Danh sách Entry (Flat ChunkZip)");
            lblTree.Dock        = DockStyle.Top;

            tvFiles             = new TreeView();
            tvFiles.Dock        = DockStyle.Fill;
            tvFiles.HideSelection = false;
            tvFiles.BorderStyle = BorderStyle.None;
            tvFiles.AfterSelect += OnTreeSelect;
            tvFiles.NodeMouseDoubleClick += (s, e) => OnRename(s, e);

            var ctx = new ContextMenuStrip();
            ctx.Items.Add("Thêm file mới...",       null, OnAdd);
            ctx.Items.Add("Thay thế entry...",       null, OnReplace);
            ctx.Items.Add("Xóa entry",               null, OnDelete);
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add("Di chuyển lên",           null, OnMoveUp);
            ctx.Items.Add("Di chuyển xuống",         null, OnMoveDown);
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add("Xuất entry ra file...",   null, OnExtract);
            ctx.Items.Add("Xuất tất cả...",          null, OnExtractAll);
            ctx.Items.Add(new ToolStripSeparator());
            ctx.Items.Add("Đổi tên entry...",        null, OnRename);
            tvFiles.ContextMenuStrip = ctx;

            panelLeft.Controls.Add(tvFiles);
            panelLeft.Controls.Add(lblTree);

            var splitter        = new Splitter();
            splitter.Dock       = DockStyle.Left;
            splitter.Width      = 4;

            // Right panel
            var panelRight      = new Panel();
            panelRight.Dock     = DockStyle.Fill;

            lvInfo              = new ListView();
            lvInfo.Dock         = DockStyle.Top;
            lvInfo.Height       = 155;
            lvInfo.View         = View.Details;
            lvInfo.FullRowSelect = true;
            lvInfo.GridLines    = true;
            lvInfo.HeaderStyle  = ColumnHeaderStyle.Nonclickable;
            lvInfo.Columns.Add("Thuộc tính", 170);
            lvInfo.Columns.Add("Giá trị",    380);
            lvInfo.BorderStyle  = BorderStyle.FixedSingle;

            var splH            = new Splitter();
            splH.Dock           = DockStyle.Top;
            splH.Height         = 4;

            var lblHex          = MkHeader("  Hex Preview (512 byte đầu)");
            lblHex.Dock         = DockStyle.Top;

            rtbHex              = new RichTextBox();
            rtbHex.Dock         = DockStyle.Fill;
            rtbHex.ReadOnly     = true;
            rtbHex.Font         = new Font("Courier New", 8.5f);
            rtbHex.BackColor    = Color.FromArgb(22, 22, 30);
            rtbHex.ForeColor    = Color.FromArgb(130, 220, 130);
            rtbHex.BorderStyle  = BorderStyle.None;
            rtbHex.WordWrap     = false;

            panelRight.Controls.Add(rtbHex);
            panelRight.Controls.Add(lblHex);
            panelRight.Controls.Add(splH);
            panelRight.Controls.Add(lvInfo);

            statusStrip         = new StatusStrip();
            lblStatus           = new ToolStripStatusLabel("Sẵn sàng.");
            lblStatus.Spring    = true;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            statusStrip.Items.Add(lblStatus);

            Controls.Add(panelRight);
            Controls.Add(splitter);
            Controls.Add(panelLeft);
            Controls.Add(lblSha);
            Controls.Add(toolbar);
            Controls.Add(menuStrip);
            Controls.Add(statusStrip);
            MainMenuStrip = menuStrip;
        }

        static Label MkHeader(string t)
        {
            return new Label {
                Text      = t,
                Height    = 22,
                BackColor = Color.FromArgb(45, 55, 85),
                ForeColor = Color.White,
                Font      = new Font("Segoe UI", 9f, FontStyle.Bold),
                TextAlign = ContentAlignment.MiddleLeft
            };
        }

        static void Btn(ToolStrip ts, string label, EventHandler h)
        {
            var b = new ToolStripButton(label);
            b.Click += h;
            ts.Items.Add(b);
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
                if (raw.Length < HEADER_SIZE + 8)
                    throw new Exception("File quá nhỏ.");

                // Kiểm tra SHA-256
                byte[] embeddedSha = new byte[32];
                Buffer.BlockCopy(raw, 0, embeddedSha, 0, 32);
                byte[] computedSha = Sha256Of(raw, HEADER_SIZE, raw.Length - HEADER_SIZE);
                shaOk = BytesEqual(embeddedSha, computedSha);

                // Quét tất cả chunkzip blocks
                List<FlatEntry> parsed = ScanBlocks(raw);
                if (parsed.Count == 0)
                    throw new Exception("Không tìm thấy chunkzip block nào.");

                currentFile = path;
                entries     = parsed;
                dirty       = false;
                RefreshTree();

                // Update SHA bar
                lblSha.BackColor = shaOk ? Color.FromArgb(20, 50, 20) : Color.FromArgb(60, 20, 20);
                lblSha.ForeColor = shaOk ? Color.LightGreen : Color.OrangeRed;
                lblSha.Text      = string.Format("  SHA-256: {0}   [{1}]",
                                   BitConverter.ToString(embeddedSha).Replace("-","").ToLower(),
                                   shaOk ? "OK ✓" : "KHÔNG KHỚP ⚠");

                Text = string.Format("Flat ChunkZip Explorer – {0}", Path.GetFileName(path));
                SetStatus(string.Format("Đã tải {0} entry. SHA-256: {1}",
                          entries.Count, shaOk ? "OK ✓" : "KHÔNG KHỚP ⚠"));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi tải file:\n" + ex.Message, "Lỗi",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        List<FlatEntry> ScanBlocks(byte[] raw)
        {
            var result = new List<FlatEntry>();
            int pos    = HEADER_SIZE;
            int idx    = 0;

            while (pos < raw.Length - 8)
            {
                if (raw[pos] == 'c' && raw[pos+1] == 'h' && raw[pos+2] == 'u' &&
                    raw[pos+3] == 'n' && raw[pos+4] == 'k')
                {
                    int endPos;
                    try
                    {
                        byte[] data = CzDecompress(raw, pos, out endPos);
                        string ext  = DetectExt(data);
                        result.Add(new FlatEntry
                        {
                            Index          = idx,
                            OffsetInFile   = pos,
                            CompressedSize = endPos - pos,
                            Data           = data,
                            Name           = string.Format("file_{0:D4}{1}", idx, ext),
                            Modified       = false
                        });
                        pos = AlignUp(endPos, ALIGN);
                        idx++;
                        continue;
                    }
                    catch { }
                }
                pos++;
            }
            return result;
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
                    dlg.FileName = Path.GetFileName(currentFile);
                if (dlg.ShowDialog() != DialogResult.OK) return;
                SaveFile(dlg.FileName);
                currentFile = dlg.FileName;
                Text = "Flat ChunkZip Explorer – " + Path.GetFileName(currentFile);
            }
        }

        void SaveFile(string path)
        {
            if (entries.Count == 0)
            {
                MessageBox.Show("Không có entry nào.", "Thông báo",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                using (MemoryStream body = new MemoryStream())
                using (BinaryWriter bw   = new BinaryWriter(body))
                {
                    foreach (FlatEntry ent in entries)
                    {
                        byte[] block = CzCompress(ent.Data);
                        bw.Write(block);
                        PadToAlign(bw, ALIGN);
                    }

                    byte[] bodyBytes  = body.ToArray();
                    byte[] sha        = Sha256Of(bodyBytes, 0, bodyBytes.Length);
                    byte[] header     = new byte[HEADER_SIZE]; // zeros
                    Buffer.BlockCopy(sha, 0, header, 0, 32);

                    using (FileStream fs = File.Create(path))
                    {
                        fs.Write(header,    0, header.Length);
                        fs.Write(bodyBytes, 0, bodyBytes.Length);
                    }
                }

                dirty = false;
                foreach (FlatEntry ent in entries) ent.Modified = false;
                RefreshTree();

                // Cập nhật SHA bar
                byte[] raw2 = File.ReadAllBytes(path);
                byte[] sha2 = Sha256Of(raw2, HEADER_SIZE, raw2.Length - HEADER_SIZE);
                shaOk = true;
                lblSha.BackColor = Color.FromArgb(20, 50, 20);
                lblSha.ForeColor = Color.LightGreen;
                lblSha.Text      = string.Format("  SHA-256: {0}   [OK ✓]",
                                   BitConverter.ToString(sha2).Replace("-","").ToLower());

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
            lblSha.BackColor = Color.FromArgb(30, 40, 30);
            lblSha.ForeColor = Color.LightGreen;
            lblSha.Text      = "  SHA-256: (chưa mở file)";
            Text = "Flat ChunkZip Explorer – EA BIG Tool";
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
                    entries.Add(new FlatEntry
                    {
                        Index    = entries.Count,
                        Data     = data,
                        Name     = Path.GetFileName(f),
                        Modified = true
                    });
                }
                MarkDirty(); RefreshTree();
                SetStatus(string.Format("Đã thêm {0} file.", dlg.FileNames.Length));
            }
        }

        void OnReplace(object sender, EventArgs e)
        {
            FlatEntry ent = GetSelected();
            if (ent == null) { NeedSel(); return; }
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title  = "Thay thế \"" + ent.Name + "\" bằng file:";
                dlg.Filter = "All files (*.*)|*.*";
                if (dlg.ShowDialog() != DialogResult.OK) return;
                ent.Data     = File.ReadAllBytes(dlg.FileName);
                ent.Modified = true;
                MarkDirty(); RefreshTree(); ShowInfo(ent);
                SetStatus(string.Format("Đã thay thế [{0}].", ent.Name));
            }
        }

        void OnDelete(object sender, EventArgs e)
        {
            FlatEntry ent = GetSelected();
            if (ent == null) { NeedSel(); return; }
            if (MessageBox.Show(string.Format("Xóa entry [{0}]?", ent.Name),
                "Xác nhận", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                return;
            int idx = ent.Index;
            entries.RemoveAt(idx);
            Reindex();
            MarkDirty(); RefreshTree();
            lvInfo.Items.Clear(); rtbHex.Clear();
            if (tvFiles.Nodes.Count > 0 && tvFiles.Nodes[0].Nodes.Count > 0)
            {
                int next = Math.Min(idx, tvFiles.Nodes[0].Nodes.Count - 1);
                tvFiles.SelectedNode = tvFiles.Nodes[0].Nodes[next];
            }
            SetStatus(string.Format("Đã xóa [{0}]. Còn {1} entry.", ent.Name, entries.Count));
        }

        void OnMoveUp(object sender, EventArgs e)   { MoveEntry(-1); }
        void OnMoveDown(object sender, EventArgs e)  { MoveEntry(+1); }

        void MoveEntry(int dir)
        {
            FlatEntry ent = GetSelected();
            if (ent == null) return;
            int idx  = ent.Index;
            int dest = idx + dir;
            if (dest < 0 || dest >= entries.Count) return;
            entries.RemoveAt(idx);
            entries.Insert(dest, ent);
            Reindex();
            MarkDirty(); RefreshTree();
            // Giữ selection
            if (tvFiles.Nodes.Count > 0)
                tvFiles.SelectedNode = tvFiles.Nodes[0].Nodes[dest];
        }

        void OnExtract(object sender, EventArgs e)
        {
            FlatEntry ent = GetSelected();
            if (ent == null) { NeedSel(); return; }
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title    = "Xuất entry";
                dlg.FileName = ent.Name;
                dlg.Filter   = "All files (*.*)|*.*";
                if (dlg.ShowDialog() != DialogResult.OK) return;
                File.WriteAllBytes(dlg.FileName, ent.Data);
                SetStatus(string.Format("Đã xuất [{0}] ({1:N0} bytes).", ent.Name, ent.Data.Length));
            }
        }

        void OnExtractAll(object sender, EventArgs e)
        {
            if (entries.Count == 0) { MessageBox.Show("Không có entry."); return; }
            using (var dlg = new FolderBrowserDialog())
            {
                dlg.Description = "Chọn thư mục xuất tất cả entry";
                if (dlg.ShowDialog() != DialogResult.OK) return;

                // Ghi manifest
                var manifest = new StringBuilder();
                manifest.AppendLine("# Flat ChunkZip Manifest");
                manifest.AppendLine(string.Format("# Source: {0}", currentFile ?? "unknown"));
                manifest.AppendLine("# Format: INDEX|NAME");
                manifest.AppendLine();

                for (int i = 0; i < entries.Count; i++)
                {
                    File.WriteAllBytes(Path.Combine(dlg.SelectedPath, entries[i].Name), entries[i].Data);
                    manifest.AppendLine(string.Format("{0}|{1}", i, entries[i].Name));
                }
                File.WriteAllText(Path.Combine(dlg.SelectedPath, "_manifest.txt"),
                                  manifest.ToString(), Encoding.UTF8);
                SetStatus(string.Format("Đã xuất {0} entry vào: {1}", entries.Count, dlg.SelectedPath));
            }
        }

        void OnRename(object sender, EventArgs e)
        {
            FlatEntry ent = GetSelected();
            if (ent == null) return;
            string newName = Microsoft.VisualBasic.Interaction.InputBox(
                "Nhập tên mới cho entry:", "Đổi tên", ent.Name);
            if (string.IsNullOrWhiteSpace(newName) || newName == ent.Name) return;
            ent.Name     = newName;
            ent.Modified = true;
            MarkDirty(); RefreshTree(); ShowInfo(ent);
        }

        // ════════════════════════════════════════════════════════════════════════
        //  TREEVIEW & INFO
        // ════════════════════════════════════════════════════════════════════════
        void RefreshTree()
        {
            string selTag = tvFiles.SelectedNode != null
                            ? tvFiles.SelectedNode.Tag as string : null;

            tvFiles.BeginUpdate();
            tvFiles.Nodes.Clear();

            string rootLabel = currentFile != null
                ? Path.GetFileName(currentFile) + (dirty ? "  *" : "")
                  + string.Format("  [{0} entry]", entries.Count)
                : "(Chưa mở file)";
            var root      = new TreeNode(rootLabel);
            root.NodeFont = new Font(tvFiles.Font, FontStyle.Bold);
            root.Tag      = "__root__";

            foreach (FlatEntry ent in entries)
            {
                string lbl = string.Format("[{0:D4}]  {1}  [{2:N0} B]",
                             ent.Index, ent.Name, ent.Data.Length);
                if (ent.Modified) lbl += "  ✎";
                var node       = new TreeNode(lbl);
                node.Tag       = ent.Index.ToString();
                node.ForeColor = ent.Modified ? Color.DodgerBlue : Color.Empty;
                root.Nodes.Add(node);
            }

            tvFiles.Nodes.Add(root);
            root.Expand();

            if (selTag != null && selTag != "__root__")
                foreach (TreeNode n in root.Nodes)
                    if (n.Tag as string == selTag) { tvFiles.SelectedNode = n; break; }

            tvFiles.EndUpdate();
        }

        void OnTreeSelect(object sender, TreeViewEventArgs e)
        {
            FlatEntry ent = GetSelected();
            if (ent == null) { lvInfo.Items.Clear(); rtbHex.Clear(); return; }
            ShowInfo(ent);
        }

        void ShowInfo(FlatEntry ent)
        {
            lvInfo.Items.Clear();
            AddRow("Index",             ent.Index.ToString());
            AddRow("Tên entry",         ent.Name);
            AddRow("Kích thước",        string.Format("{0:N0} bytes", ent.Data.Length));
            AddRow("Offset gốc",        string.Format("0x{0:X}  (block #{1})", ent.OffsetInFile, ent.Index));
            AddRow("Compressed size",   string.Format("{0:N0} bytes  (tỉ lệ {1:0.0}%)",
                                        ent.CompressedSize,
                                        ent.Data.Length > 0
                                        ? 100.0 * ent.CompressedSize / ent.Data.Length : 0));
            AddRow("Kiểu dữ liệu",      DetectExt(ent.Data));
            AddRow("Trạng thái",        ent.Modified ? "Đã sửa đổi ✎" : "Gốc");

            rtbHex.Text = HexDump(ent.Data, Math.Min(512, ent.Data.Length));
        }

        void AddRow(string k, string v)
        {
            var item = new ListViewItem(k);
            item.SubItems.Add(v);
            lvInfo.Items.Add(item);
        }

        string HexDump(byte[] data, int length)
        {
            var sb = new StringBuilder();
            sb.AppendLine("  Offset    00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F   ASCII");
            sb.AppendLine("  " + new string('─', 74));
            for (int r = 0; r < (length + 15) / 16; r++)
            {
                int rs = r * 16;
                sb.Append(string.Format("  {0:X8}  ", rs));
                var asc = new StringBuilder();
                for (int c = 0; c < 16; c++)
                {
                    if (c == 8) sb.Append(" ");
                    int i = rs + c;
                    if (i < length) { byte b = data[i]; sb.Append(string.Format("{0:X2} ", b)); asc.Append(b>=32&&b<127?(char)b:'.'); }
                    else            { sb.Append("   "); asc.Append(' '); }
                }
                sb.Append("  "); sb.AppendLine(asc.ToString());
            }
            if (data.Length > length)
                sb.AppendLine(string.Format("\n  ... còn {0:N0} bytes", data.Length - length));
            return sb.ToString();
        }

        // ════════════════════════════════════════════════════════════════════════
        //  CHUNKZIP CORE (raw deflate, no zlib header)
        // ════════════════════════════════════════════════════════════════════════
        static byte[] CzDecompress(byte[] raw, int start, out int endOffset)
        {
            int pos = start;
            string ct = Encoding.ASCII.GetString(raw, pos, 8).TrimEnd('\0');
            pos += 8;
            if (ct != "chunkzip")
                throw new NotSupportedException("Not chunkzip: " + ct);

            uint dummy = BE32(raw, pos); pos += 4;
            uint fullsize; int bt;
            if (dummy <= 2) { fullsize = BE32(raw, pos); pos += 4; bt = 0; }
            else            { fullsize = dummy; bt = 1; }

            uint chunkSize = BE32(raw, pos); pos += 4;
            uint numChunks;
            if (bt == 0) { numChunks = BE32(raw, pos); pos += 4; pos += 16; }
            else         { numChunks = (fullsize + chunkSize - 1) / chunkSize; }

            using (var ms = new MemoryStream())
            {
                for (uint c = 0; c < numChunks; c++)
                {
                    uint zsize = BE32(raw, pos); pos += 4;
                    bool comp  = true;
                    if (bt == 0) { uint flag = BE32(raw, pos); pos += 4; if (flag == 4) comp = false; }

                    if (comp)
                    {
                        using (var src = new MemoryStream(raw, pos, (int)zsize))
                        using (var ds  = new DeflateStream(src, CompressionMode.Decompress))
                        using (var tmp = new MemoryStream())
                        { ds.CopyTo(tmp); byte[] dec = tmp.ToArray(); ms.Write(dec, 0, dec.Length); }
                    }
                    else ms.Write(raw, pos, (int)zsize);
                    pos += (int)zsize;
                }
                endOffset = pos;
                return ms.ToArray();
            }
        }

        static byte[] CzCompress(byte[] data)
        {
            int cs = DEFAULT_CHUNK_SIZE;
            int n  = Math.Max(1, (data.Length + cs - 1) / cs);
            var chunks = new List<byte[]>();
            for (int i = 0; i < n; i++)
            {
                int off = i * cs, len = Math.Min(cs, data.Length - off);
                using (var ms = new MemoryStream())
                using (var ds = new DeflateStream(ms, CompressionLevel.Optimal))
                { ds.Write(data, off, len); ds.Close(); chunks.Add(ms.ToArray()); }
            }
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                bw.Write(Encoding.ASCII.GetBytes("chunkzip"));
                W32BE(bw, 2); W32BE(bw, (uint)data.Length);
                W32BE(bw, (uint)cs); W32BE(bw, (uint)n);
                W32BE(bw, 16); bw.Write(new byte[12]);
                foreach (byte[] ch in chunks) { W32BE(bw, (uint)ch.Length); W32BE(bw, 1); bw.Write(ch); }
                return ms.ToArray();
            }
        }

        // ════════════════════════════════════════════════════════════════════════
        //  UTILITIES
        // ════════════════════════════════════════════════════════════════════════
        static uint BE32(byte[] b, int o)
        { return (uint)((b[o]<<24)|(b[o+1]<<16)|(b[o+2]<<8)|b[o+3]); }

        static void W32BE(BinaryWriter bw, uint v)
        { bw.Write((byte)(v>>24)); bw.Write((byte)(v>>16)); bw.Write((byte)(v>>8)); bw.Write((byte)v); }

        static int AlignUp(int v, int a) { return (v + a - 1) & ~(a - 1); }

        static void PadToAlign(BinaryWriter bw, int a)
        { long r = bw.BaseStream.Position % a; if (r != 0) bw.Write(new byte[a - r]); }

        static byte[] Sha256Of(byte[] data, int off, int count)
        {
            byte[] seg = new byte[count];
            Buffer.BlockCopy(data, off, seg, 0, count);
            using (SHA256 sha = SHA256.Create()) return sha.ComputeHash(seg);
        }

        static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static string DetectExt(byte[] d)
        {
            if (d == null || d.Length < 4) return ".bin";
            if (d[0]=='<')                                           return ".xml";
            if (d[0]=='{')                                           return ".json";
            if (d[0]=='V' && d[1]=='i' && d[2]=='V' && d[3]=='4') return ".big";
            if (d[0]==0x89&&d[1]=='P'&&d[2]=='N'&&d[3]=='G')       return ".png";
            if (d[0]==0xFF&&d[1]==0xD8)                              return ".jpg";
            if (d[0]=='D'&&d[1]=='D'&&d[2]=='S'&&d[3]==' ')        return ".dds";
            if (d[0]=='R'&&d[1]=='I'&&d[2]=='F'&&d[3]=='F')        return ".riff";
            if (d[0]=='E'&&d[1]=='B'&&d[2]=='O')                   return ".ebo";
            if (d[0]=='P'&&d[1]=='K')                               return ".zip";
            if (d[0]==0x1F&&d[1]==0x8B)                             return ".gz";
            return ".bin";
        }

        FlatEntry GetSelected()
        {
            TreeNode n = tvFiles.SelectedNode;
            if (n == null || n.Tag == null || n.Tag.ToString() == "__root__") return null;
            int idx; if (!int.TryParse(n.Tag.ToString(), out idx)) return null;
            if (idx < 0 || idx >= entries.Count) return null;
            return entries[idx];
        }

        void Reindex() { for (int i = 0; i < entries.Count; i++) entries[i].Index = i; }
        void MarkDirty() { dirty = true; }
        void SetStatus(string m) { lblStatus.Text = m; }
        void NeedSel() { MessageBox.Show("Hãy chọn một entry.", "Thông báo", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        bool ConfirmDiscard()
        {
            return MessageBox.Show("Có thay đổi chưa lưu. Tiếp tục?",
                "Chưa lưu", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
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
