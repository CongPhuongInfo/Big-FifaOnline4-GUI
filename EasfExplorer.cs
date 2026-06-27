/*
 * EasfExplorer.cs - WinForms GUI cho EA BIG / EASF Container
 * .NET Framework 4.x | C# 5 Compatible
 *
 * Hỗ trợ 2 sub-format EASF:
 *  - WithHeader : [32-byte SHA256][32-byte zeros][EASF block 0][EASF block 1]...
 *                 (data_ini.big, v.v.)
 *  - RawEASF   : [EASF block 0][EASF block 1]...  (không có outer header)
 *                 (data_second_VN.big, v.v.)
 * Tool tự phát hiện format khi mở, lưu lại đúng format gốc.
 *
 * Tính năng:
 *  - Mở file .big, hiển thị danh sách EASF block theo TreeView
 *  - Xem nội dung block (hex dump + text) trong panel bên phải
 *  - Thêm block mới từ file ngoài
 *  - Thay thế (Edit) block bằng file khác
 *  - Xóa block
 *  - Lưu lại file .big (rebuild + re-encrypt, giữ đúng format gốc)
 *  - Chọn game key: default / fifa15 / fifa16
 */

using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace EasfExplorer
{
    // ─── Dữ liệu một EASF block đã giải mã ──────────────────────────────────
    class EasfBlock
    {
        public int    Index;
        public int    OffsetInFile;
        public string KeyId;
        public bool   DigestOk;
        public byte[] PlainData;   // dữ liệu đã giải mã
        public bool   Modified;    // đánh dấu thay đổi
    }

    // ════════════════════════════════════════════════════════════════════════
    //  MAIN FORM
    // ════════════════════════════════════════════════════════════════════════
    class MainForm : Form
    {
        // ── Controls ─────────────────────────────────────────────────────
        TreeView  tvBlocks;
        ListView  lvInfo;
        RichTextBox rtbHex;
        StatusStrip statusStrip;
        ToolStripStatusLabel lblStatus;
        MenuStrip  menuStrip;
        ComboBox   cmbGame;
        Panel      panelRight;
        Splitter   splitter;
        Label      lblHexTitle;

        // ── State ─────────────────────────────────────────────────────────
        string           currentFilePath = null;
        List<EasfBlock>  blocks          = new List<EasfBlock>();
        byte[]           currentKey      = null;
        bool             dirty           = false;
        bool             hasOuterHeader  = false;  // true=WithHeader, false=RawEASF

        // ── Extra UI ──────────────────────────────────────────────────────
        Label            lblFormatInfo;

        // ── AES Keys ──────────────────────────────────────────────────────
        static readonly byte[] KeyFifa1516 = {
            0x24,0x9B,0xF2,0x7A,0xF5,0xD7,0x48,0x7B,
            0x15,0x78,0xD8,0x33,0xF2,0xDE,0x39,0xB5
        };
        static readonly byte[] KeyDefault = {
            0x24,0x91,0x85,0xE3,0x70,0x7B,0xD8,0x83,
            0xCE,0xA5,0xC5,0x11,0xF5,0xD4,0x67,0xF2
        };

        const int EASF_HEADER_SIZE  = 48;
        const int OUTER_HEADER_SIZE = 64;
        const int BLOCK_ALIGN       = 0x40;

        // ════════════════════════════════════════════════════════════════
        public MainForm()
        {
            Text            = "EASF Explorer – EA BIG File Tool";
            Size            = new Size(1000, 650);
            MinimumSize     = new Size(750, 500);
            StartPosition   = FormStartPosition.CenterScreen;
            Font            = new Font("Segoe UI", 9f);

            BuildUI();
            currentKey = KeyDefault;
        }

        // ════════════════════════════════════════════════════════════════
        //  BUILD UI
        // ════════════════════════════════════════════════════════════════
        void BuildUI()
        {
            // ── MenuStrip ─────────────────────────────────────────────
            menuStrip = new MenuStrip();
            menuStrip.Dock = DockStyle.Top;

            var miFile  = new ToolStripMenuItem("&File");
            var miOpen  = new ToolStripMenuItem("&Mở file .big...",  null, OnOpen,  Keys.Control | Keys.O);
            var miSave  = new ToolStripMenuItem("&Lưu",              null, OnSave,  Keys.Control | Keys.S);
            var miSaveAs= new ToolStripMenuItem("Lưu &As...",        null, OnSaveAs);
            var miClose = new ToolStripMenuItem("&Đóng file",        null, OnClose);
            var miExit  = new ToolStripMenuItem("Thoá&t",            null, (s,e) => Close());
            miFile.DropDownItems.AddRange(new ToolStripItem[]{
                miOpen, miSave, miSaveAs, new ToolStripSeparator(), miClose,
                new ToolStripSeparator(), miExit
            });

            var miBlock  = new ToolStripMenuItem("&Block");
            var miAdd    = new ToolStripMenuItem("&Thêm block mới...",    null, OnAddBlock,     Keys.Insert);
            var miReplace= new ToolStripMenuItem("&Thay thế block...",    null, OnReplaceBlock, Keys.Control | Keys.R);
            var miDelete = new ToolStripMenuItem("&Xóa block",            null, OnDeleteBlock,  Keys.Delete);
            var miExtract= new ToolStripMenuItem("&Xuất block ra file...",null, OnExtractBlock, Keys.Control | Keys.E);
            miBlock.DropDownItems.AddRange(new ToolStripItem[]{
                miAdd, miReplace, miDelete, new ToolStripSeparator(), miExtract
            });

            var miFormat       = new ToolStripMenuItem("F&ormat");
            var miToWithHeader = new ToolStripMenuItem("Chuyển sang WithHeader (thêm SHA-256 header)", null, (s,e)=>{
                if (blocks.Count == 0) return;
                hasOuterHeader = true;
                dirty = true;
                lblFormatInfo.BackColor = Color.FromArgb(30, 30, 55);
                lblFormatInfo.ForeColor = Color.LightSkyBlue;
                lblFormatInfo.Text = "  Format: WithHeader (sẽ ghi SHA-256 khi lưu)  [đã thay đổi *]";
                SetStatus("Đã chuyển sang WithHeader — nhớ Lưu lại.");
            });
            var miToRaw = new ToolStripMenuItem("Chuyển sang RawEASF (bỏ outer header)", null, (s,e)=>{
                if (blocks.Count == 0) return;
                hasOuterHeader = false;
                dirty = true;
                lblFormatInfo.BackColor = Color.FromArgb(30, 30, 55);
                lblFormatInfo.ForeColor = Color.LightSkyBlue;
                lblFormatInfo.Text = "  Format: RawEASF (không có outer header)  [đã thay đổi *]";
                SetStatus("Đã chuyển sang RawEASF — nhớ Lưu lại.");
            });
            miFormat.DropDownItems.Add(miToWithHeader);
            miFormat.DropDownItems.Add(miToRaw);

            menuStrip.Items.Add(miFile);
            menuStrip.Items.Add(miBlock);
            menuStrip.Items.Add(miFormat);

            // ── Toolbar ───────────────────────────────────────────────
            var toolbar = new ToolStrip();
            toolbar.Dock = DockStyle.Top;

            var btnOpen    = new ToolStripButton("📂 Mở");
            btnOpen.Click += OnOpen;
            var btnSave    = new ToolStripButton("💾 Lưu");
            btnSave.Click += OnSave;
            var btnAdd     = new ToolStripButton("➕ Thêm");
            btnAdd.Click  += OnAddBlock;
            var btnReplace = new ToolStripButton("✏ Thay thế");
            btnReplace.Click += OnReplaceBlock;
            var btnDelete  = new ToolStripButton("🗑 Xóa");
            btnDelete.Click += OnDeleteBlock;
            var btnExtract = new ToolStripButton("📤 Xuất");
            btnExtract.Click += OnExtractBlock;

            toolbar.Items.Add(btnOpen);
            toolbar.Items.Add(btnSave);
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(btnAdd);
            toolbar.Items.Add(btnReplace);
            toolbar.Items.Add(btnDelete);
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(btnExtract);
            toolbar.Items.Add(new ToolStripSeparator());
            toolbar.Items.Add(new ToolStripLabel("  Game key: "));

            // ComboBox trong toolbar
            var cmbHost = new ToolStripControlHost(BuildGameCombo());
            toolbar.Items.Add(cmbHost);

            // ── Format info bar ───────────────────────────────────────
            lblFormatInfo           = new Label();
            lblFormatInfo.Dock      = DockStyle.Top;
            lblFormatInfo.Height    = 22;
            lblFormatInfo.BackColor = Color.FromArgb(30, 35, 50);
            lblFormatInfo.ForeColor = Color.LightGray;
            lblFormatInfo.Font      = new Font("Courier New", 8.5f);
            lblFormatInfo.TextAlign = ContentAlignment.MiddleLeft;
            lblFormatInfo.Text      = "  Format: (chưa mở file)";

            // ── Left panel: TreeView ──────────────────────────────────
            var panelLeft = new Panel();
            panelLeft.Dock  = DockStyle.Left;
            panelLeft.Width = 230;
            panelLeft.BorderStyle = BorderStyle.FixedSingle;

            var lblTree = new Label();
            lblTree.Text      = "  Danh sách Block";
            lblTree.Dock      = DockStyle.Top;
            lblTree.Height    = 22;
            lblTree.BackColor = Color.FromArgb(50, 50, 80);
            lblTree.ForeColor = Color.White;
            lblTree.Font      = new Font("Segoe UI", 9f, FontStyle.Bold);
            lblTree.TextAlign = ContentAlignment.MiddleLeft;

            tvBlocks = new TreeView();
            tvBlocks.Dock           = DockStyle.Fill;
            tvBlocks.HideSelection  = false;
            tvBlocks.BorderStyle    = BorderStyle.None;
            tvBlocks.AfterSelect   += OnTreeSelect;

            // Context menu cho TreeView
            var ctxTree = new ContextMenuStrip();
            ctxTree.Items.Add("Thêm block mới...",  null, OnAddBlock);
            ctxTree.Items.Add("Thay thế block...",  null, OnReplaceBlock);
            ctxTree.Items.Add("Xóa block",          null, OnDeleteBlock);
            ctxTree.Items.Add(new ToolStripSeparator());
            ctxTree.Items.Add("Xuất block ra file...", null, OnExtractBlock);
            tvBlocks.ContextMenuStrip = ctxTree;

            panelLeft.Controls.Add(tvBlocks);
            panelLeft.Controls.Add(lblTree);

            // ── Splitter ─────────────────────────────────────────────
            splitter = new Splitter();
            splitter.Dock = DockStyle.Left;
            splitter.Width = 4;

            // ── Right panel ───────────────────────────────────────────
            panelRight = new Panel();
            panelRight.Dock = DockStyle.Fill;

            // ListView thông tin block
            lvInfo = new ListView();
            lvInfo.Dock     = DockStyle.Top;
            lvInfo.Height   = 130;
            lvInfo.View     = View.Details;
            lvInfo.FullRowSelect = true;
            lvInfo.GridLines = true;
            lvInfo.HeaderStyle = ColumnHeaderStyle.Nonclickable;
            lvInfo.Columns.Add("Thuộc tính", 160);
            lvInfo.Columns.Add("Giá trị",    350);
            lvInfo.BorderStyle = BorderStyle.FixedSingle;

            var splitterH = new Splitter();
            splitterH.Dock = DockStyle.Top;
            splitterH.Height = 4;

            lblHexTitle = new Label();
            lblHexTitle.Text      = "  Hex Preview";
            lblHexTitle.Dock      = DockStyle.Top;
            lblHexTitle.Height    = 22;
            lblHexTitle.BackColor = Color.FromArgb(50, 50, 80);
            lblHexTitle.ForeColor = Color.White;
            lblHexTitle.Font      = new Font("Segoe UI", 9f, FontStyle.Bold);
            lblHexTitle.TextAlign = ContentAlignment.MiddleLeft;

            rtbHex = new RichTextBox();
            rtbHex.Dock      = DockStyle.Fill;
            rtbHex.ReadOnly  = true;
            rtbHex.Font      = new Font("Courier New", 8.5f);
            rtbHex.BackColor = Color.FromArgb(30, 30, 30);
            rtbHex.ForeColor = Color.LightGreen;
            rtbHex.BorderStyle = BorderStyle.None;
            rtbHex.WordWrap  = false;

            panelRight.Controls.Add(rtbHex);
            panelRight.Controls.Add(lblHexTitle);
            panelRight.Controls.Add(splitterH);
            panelRight.Controls.Add(lvInfo);

            // ── StatusStrip ──────────────────────────────────────────
            statusStrip = new StatusStrip();
            lblStatus = new ToolStripStatusLabel("Sẵn sàng. Mở file .big để bắt đầu.");
            lblStatus.Spring = true;
            lblStatus.TextAlign = ContentAlignment.MiddleLeft;
            statusStrip.Items.Add(lblStatus);

            // ── Assemble ─────────────────────────────────────────────
            Controls.Add(panelRight);
            Controls.Add(splitter);
            Controls.Add(panelLeft);
            Controls.Add(lblFormatInfo);
            Controls.Add(toolbar);
            Controls.Add(menuStrip);
            Controls.Add(statusStrip);
            MainMenuStrip = menuStrip;
        }

        ComboBox BuildGameCombo()
        {
            cmbGame = new ComboBox();
            cmbGame.DropDownStyle = ComboBoxStyle.DropDownList;
            cmbGame.Width = 90;
            cmbGame.Items.AddRange(new object[]{ "default", "fifa15", "fifa16" });
            cmbGame.SelectedIndex = 0;
            cmbGame.SelectedIndexChanged += (s, e) =>
            {
                string g = cmbGame.SelectedItem.ToString();
                currentKey = (g == "fifa15" || g == "fifa16") ? KeyFifa1516 : KeyDefault;
                SetStatus("Game key đổi thành: " + g);
            };
            return cmbGame;
        }

        // ════════════════════════════════════════════════════════════════
        //  FILE OPERATIONS
        // ════════════════════════════════════════════════════════════════
        void OnOpen(object sender, EventArgs e)
        {
            if (dirty && !ConfirmDiscardChanges()) return;

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
                if (raw.Length < EASF_HEADER_SIZE)
                    throw new Exception("File quá nhỏ hoặc không phải định dạng EASF.");

                // ── Auto-detect format ────────────────────────────────────
                // WithHeader : byte 0..3 KHÔNG phải 'EASF', byte 64..67 là 'EASF'
                // RawEASF   : byte 0..3 là 'EASF'
                bool rawIsEasf = (raw[0]==0x45 && raw[1]==0x41 && raw[2]==0x53 && raw[3]==0x46);
                bool hasHeader64Easf = raw.Length >= OUTER_HEADER_SIZE + EASF_HEADER_SIZE
                                    && raw[64]==0x45 && raw[65]==0x41 && raw[66]==0x53 && raw[67]==0x46;

                if (rawIsEasf)
                    hasOuterHeader = false;        // RawEASF
                else if (hasHeader64Easf)
                    hasOuterHeader = true;         // WithHeader
                else
                    throw new Exception("Không nhận ra định dạng EASF.\n"
                        + "Byte[0:4] = " + BitConverter.ToString(raw, 0, 4)
                        + "  Byte[64:68] = " + (raw.Length>67 ? BitConverter.ToString(raw,64,4) : "N/A"));

                // ── Kiểm tra outer SHA-256 (chỉ với WithHeader) ───────────
                bool outerOk = false;
                if (hasOuterHeader)
                {
                    byte[] embedded = new byte[32];
                    Buffer.BlockCopy(raw, 0, embedded, 0, 32);
                    byte[] computed = ComputeSHA256(raw, OUTER_HEADER_SIZE, raw.Length - OUTER_HEADER_SIZE);
                    outerOk = BytesEqual(embedded, computed);
                }

                // ── Parse EASF blocks ─────────────────────────────────────
                var parsed = new List<EasfBlock>();
                int offset = hasOuterHeader ? OUTER_HEADER_SIZE : 0;
                int idx    = 0;

                while (offset + EASF_HEADER_SIZE <= raw.Length)
                {
                    uint sig = ReadUInt32LE(raw, offset);
                    if (sig != 0x46534145)  // 'EASF' LE
                    {
                        int next = FindNextEASF(raw, offset + 1);
                        if (next < 0) break;
                        offset = next;
                        continue;
                    }

                    uint   decSz        = ReadUInt32BE(raw, offset + 4);
                    byte[] keyIdBytes   = new byte[8];
                    Buffer.BlockCopy(raw, offset + 8, keyIdBytes, 0, 8);
                    byte[] storedDigest = new byte[32];
                    Buffer.BlockCopy(raw, offset + 16, storedDigest, 0, 32);

                    string keyId    = Encoding.ASCII.GetString(keyIdBytes).TrimEnd('\0', ' ');
                    int    encStart = offset + EASF_HEADER_SIZE;
                    int    encLen   = (int)(((decSz + 15) / 16) * 16);

                    if (encStart + encLen > raw.Length) break;

                    byte[] encData = new byte[encLen];
                    Buffer.BlockCopy(raw, encStart, encData, 0, encLen);
                    byte[] plain   = AesDecrypt(encData, currentKey, currentKey);

                    bool digestOk = false;
                    if (plain != null && plain.Length > 0)
                    {
                        if (plain.Length > (int)decSz)
                            Array.Resize(ref plain, (int)decSz);
                        byte[] calcDigest = ComputeSHA256(plain, 0, Math.Min(32, plain.Length));
                        digestOk = BytesEqual(storedDigest, calcDigest);
                    }

                    parsed.Add(new EasfBlock
                    {
                        Index        = idx,
                        OffsetInFile = offset,
                        KeyId        = keyId,
                        DigestOk     = digestOk,
                        PlainData    = plain ?? new byte[0],
                        Modified     = false
                    });

                    int rawNext = encStart + encLen;
                    offset = AlignUp(rawNext, BLOCK_ALIGN);
                    idx++;
                }

                if (parsed.Count == 0)
                    throw new Exception("Không tìm thấy block EASF nào trong file.");

                currentFilePath = path;
                blocks          = parsed;
                dirty           = false;
                RefreshTree();

                // ── Cập nhật format bar ───────────────────────────────────
                if (hasOuterHeader)
                {
                    lblFormatInfo.BackColor = outerOk
                        ? Color.FromArgb(20, 50, 20) : Color.FromArgb(60, 20, 20);
                    lblFormatInfo.ForeColor = outerOk ? Color.LightGreen : Color.OrangeRed;
                    lblFormatInfo.Text      = string.Format(
                        "  Format: WithHeader (SHA-256 outer: {0})   SHA: {1}...",
                        outerOk ? "OK ✓" : "KHÔNG KHỚP ⚠",
                        BitConverter.ToString(raw, 0, 16).Replace("-","").ToLower());
                }
                else
                {
                    lblFormatInfo.BackColor = Color.FromArgb(30, 30, 55);
                    lblFormatInfo.ForeColor = Color.LightSkyBlue;
                    lblFormatInfo.Text      = "  Format: RawEASF (không có outer SHA-256 header)";
                }

                Text = string.Format("EASF Explorer – {0}  [{1}]",
                    Path.GetFileName(path),
                    hasOuterHeader ? "WithHeader" : "RawEASF");
                SetStatus(string.Format("Đã tải {0} block(s). Format: {1}{2}",
                    blocks.Count,
                    hasOuterHeader ? "WithHeader" : "RawEASF",
                    hasOuterHeader ? (outerOk ? ", SHA-256 OK ✓" : ", SHA-256 ⚠") : ""));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi tải file:\n" + ex.Message, "Lỗi",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void OnSave(object sender, EventArgs e)
        {
            if (currentFilePath == null) { OnSaveAs(sender, e); return; }
            SaveFile(currentFilePath);
        }

        void OnSaveAs(object sender, EventArgs e)
        {
            using (var dlg = new SaveFileDialog())
            {
                dlg.Title  = "Lưu file .big";
                dlg.Filter = "EA BIG files (*.big)|*.big|All files (*.*)|*.*";
                if (currentFilePath != null)
                    dlg.FileName = Path.GetFileName(currentFilePath);
                if (dlg.ShowDialog() != DialogResult.OK) return;
                SaveFile(dlg.FileName);
                currentFilePath = dlg.FileName;
                Text = "EASF Explorer – " + Path.GetFileName(currentFilePath);
            }
        }

        void SaveFile(string path)
        {
            if (blocks.Count == 0)
            {
                MessageBox.Show("Không có block nào để lưu.", "Thông báo",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            try
            {
                using (MemoryStream body = new MemoryStream())
                {
                    foreach (EasfBlock blk in blocks)
                    {
                        byte[] easfBlock = CreateEasfBlock(blk.PlainData, currentKey);
                        body.Write(easfBlock, 0, easfBlock.Length);
                        int pos = (int)body.Position;
                        int pad = AlignUp(pos, BLOCK_ALIGN) - pos;
                        if (pad > 0) body.Write(new byte[pad], 0, pad);
                    }

                    byte[] bodyBytes = body.ToArray();

                    using (FileStream fs = File.Create(path))
                    {
                        if (hasOuterHeader)
                        {
                            // WithHeader: ghi [32-byte SHA256][32-byte zeros][body]
                            byte[] outerHash   = ComputeSHA256(bodyBytes, 0, bodyBytes.Length);
                            byte[] outerHeader = new byte[OUTER_HEADER_SIZE]; // zeros
                            Buffer.BlockCopy(outerHash, 0, outerHeader, 0, 32);
                            fs.Write(outerHeader, 0, outerHeader.Length);
                        }
                        // RawEASF: ghi body trực tiếp, không có header
                        fs.Write(bodyBytes, 0, bodyBytes.Length);
                    }
                }

                dirty = false;
                foreach (EasfBlock blk in blocks) blk.Modified = false;
                RefreshTree();
                SetStatus(string.Format("Đã lưu: {0}  ({1} block(s), format={2})",
                    path, blocks.Count, hasOuterHeader ? "WithHeader" : "RawEASF"));
            }
            catch (Exception ex)
            {
                MessageBox.Show("Lỗi lưu file:\n" + ex.Message, "Lỗi",
                                MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        void OnClose(object sender, EventArgs e)
        {
            if (dirty && !ConfirmDiscardChanges()) return;
            currentFilePath = null;
            blocks.Clear();
            dirty          = false;
            hasOuterHeader = false;
            tvBlocks.Nodes.Clear();
            lvInfo.Items.Clear();
            rtbHex.Clear();
            lblFormatInfo.BackColor = Color.FromArgb(30, 35, 50);
            lblFormatInfo.ForeColor = Color.LightGray;
            lblFormatInfo.Text      = "  Format: (chưa mở file)";
            Text = "EASF Explorer – EA BIG File Tool";
            SetStatus("Đã đóng file.");
        }

        // ════════════════════════════════════════════════════════════════
        //  BLOCK OPERATIONS
        // ════════════════════════════════════════════════════════════════
        void OnAddBlock(object sender, EventArgs e)
        {
            using (var dlg = new OpenFileDialog())
            {
                dlg.Title  = "Chọn file để thêm vào như block mới";
                dlg.Filter = "All files (*.*)|*.*";
                dlg.Multiselect = true;
                if (dlg.ShowDialog() != DialogResult.OK) return;

                foreach (string f in dlg.FileNames)
                {
                    byte[] data = File.ReadAllBytes(f);
                    blocks.Add(new EasfBlock
                    {
                        Index     = blocks.Count,
                        KeyId     = "datax",
                        DigestOk  = true,
                        PlainData = data,
                        Modified  = true
                    });
                }
                MarkDirty();
                RefreshTree();
                SetStatus(string.Format("Đã thêm {0} block(s) mới.", dlg.FileNames.Length));
            }
        }

        void OnReplaceBlock(object sender, EventArgs e)
        {
            EasfBlock blk = GetSelectedBlock();
            if (blk == null)
            {
                MessageBox.Show("Hãy chọn một block trong danh sách.", "Thông báo",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new OpenFileDialog())
            {
                dlg.Title  = string.Format("Thay thế block_{0:D3} bằng file:", blk.Index);
                dlg.Filter = "All files (*.*)|*.*";
                if (dlg.ShowDialog() != DialogResult.OK) return;

                blk.PlainData = File.ReadAllBytes(dlg.FileName);
                blk.DigestOk  = true;
                blk.Modified  = true;
                MarkDirty();
                RefreshTree();
                ShowBlockInfo(blk);
                SetStatus(string.Format("Đã thay thế block_{0:D3} bằng: {1}",
                                        blk.Index, Path.GetFileName(dlg.FileName)));
            }
        }

        void OnDeleteBlock(object sender, EventArgs e)
        {
            EasfBlock blk = GetSelectedBlock();
            if (blk == null)
            {
                MessageBox.Show("Hãy chọn một block trong danh sách.", "Thông báo",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var res = MessageBox.Show(
                string.Format("Xóa block_{0:D3} ({1:N0} bytes)?", blk.Index, blk.PlainData.Length),
                "Xác nhận xóa", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (res != DialogResult.Yes) return;

            int selIdx = blk.Index;
            blocks.RemoveAt(selIdx);
            // Re-index
            for (int i = 0; i < blocks.Count; i++) blocks[i].Index = i;
            MarkDirty();
            RefreshTree();
            lvInfo.Items.Clear();
            rtbHex.Clear();

            // Chọn lại block gần nhất
            if (tvBlocks.Nodes.Count > 0)
            {
                int next = Math.Min(selIdx, tvBlocks.Nodes.Count - 1);
                tvBlocks.SelectedNode = tvBlocks.Nodes[next];
            }
            SetStatus(string.Format("Đã xóa block_{0:D3}. Còn {1} block(s).", selIdx, blocks.Count));
        }

        void OnExtractBlock(object sender, EventArgs e)
        {
            EasfBlock blk = GetSelectedBlock();
            if (blk == null)
            {
                MessageBox.Show("Hãy chọn một block trong danh sách.", "Thông báo",
                                MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            using (var dlg = new SaveFileDialog())
            {
                dlg.Title    = string.Format("Xuất block_{0:D3} ra file", blk.Index);
                dlg.FileName = string.Format("block_{0:D3}.bin", blk.Index);
                dlg.Filter   = "Binary files (*.bin)|*.bin|All files (*.*)|*.*";
                if (dlg.ShowDialog() != DialogResult.OK) return;

                File.WriteAllBytes(dlg.FileName, blk.PlainData);
                SetStatus(string.Format("Đã xuất block_{0:D3} → {1}  ({2:N0} bytes)",
                                        blk.Index, Path.GetFileName(dlg.FileName), blk.PlainData.Length));
            }
        }

        // ════════════════════════════════════════════════════════════════
        //  TREEVIEW  &  INFO PANEL
        // ════════════════════════════════════════════════════════════════
        void RefreshTree()
        {
            string selTag = tvBlocks.SelectedNode != null
                            ? tvBlocks.SelectedNode.Tag as string : null;

            tvBlocks.BeginUpdate();
            tvBlocks.Nodes.Clear();

            TreeNode root = new TreeNode(
                currentFilePath != null
                    ? Path.GetFileName(currentFilePath) + (dirty ? " *" : "")
                    : "(Chưa mở file)");
            root.NodeFont = new Font(tvBlocks.Font, FontStyle.Bold);
            root.Tag      = "__root__";

            foreach (EasfBlock blk in blocks)
            {
                string label = string.Format("block_{0:D3}  [{1:N0} B]", blk.Index, blk.PlainData.Length);
                if (blk.Modified) label += "  ✎";
                if (!blk.DigestOk) label += "  ⚠";
                TreeNode node = new TreeNode(label);
                node.Tag      = blk.Index.ToString();
                node.ForeColor = blk.Modified ? Color.DodgerBlue
                               : (!blk.DigestOk ? Color.OrangeRed : Color.Empty);
                root.Nodes.Add(node);
            }

            tvBlocks.Nodes.Add(root);
            root.Expand();

            // Khôi phục selection
            if (selTag != null && selTag != "__root__")
            {
                foreach (TreeNode n in root.Nodes)
                {
                    if (n.Tag as string == selTag)
                    {
                        tvBlocks.SelectedNode = n;
                        break;
                    }
                }
            }

            tvBlocks.EndUpdate();
        }

        void OnTreeSelect(object sender, TreeViewEventArgs e)
        {
            EasfBlock blk = GetSelectedBlock();
            if (blk == null) { lvInfo.Items.Clear(); rtbHex.Clear(); return; }
            ShowBlockInfo(blk);
        }

        void ShowBlockInfo(EasfBlock blk)
        {
            lvInfo.Items.Clear();
            AddInfo("Block Index",    blk.Index.ToString());
            AddInfo("Kích thước",     string.Format("{0:N0} bytes", blk.PlainData.Length));
            AddInfo("Key ID",         blk.KeyId);
            AddInfo("Digest",         blk.DigestOk ? "OK ✓" : "KHÔNG KHỚP ⚠");
            AddInfo("Offset (gốc)",   string.Format("0x{0:X}", blk.OffsetInFile));
            AddInfo("File format",    hasOuterHeader ? "WithHeader (có SHA-256 outer)" : "RawEASF (không có outer header)");
            AddInfo("Trạng thái",     blk.Modified ? "Đã sửa đổi ✎" : "Gốc");

            // Hex dump (max 512 bytes)
            int  previewLen = Math.Min(512, blk.PlainData.Length);
            rtbHex.Text     = BuildHexDump(blk.PlainData, previewLen);
        }

        void AddInfo(string key, string val)
        {
            var item = new ListViewItem(key);
            item.SubItems.Add(val);
            lvInfo.Items.Add(item);
        }

        string BuildHexDump(byte[] data, int length)
        {
            var sb  = new StringBuilder();
            int rows = (length + 15) / 16;
            sb.AppendLine(string.Format("  Offset    00 01 02 03 04 05 06 07  08 09 0A 0B 0C 0D 0E 0F   ASCII"));
            sb.AppendLine("  " + new string('─', 74));
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
                    else
                    {
                        sb.Append("   ");
                        ascii.Append(' ');
                    }
                }
                sb.Append("  ");
                sb.AppendLine(ascii.ToString());
            }
            if (data.Length > length)
                sb.AppendLine(string.Format("\n  ... (còn {0:N0} bytes nữa không hiển thị)", data.Length - length));
            return sb.ToString();
        }

        // ════════════════════════════════════════════════════════════════
        //  HELPERS
        // ════════════════════════════════════════════════════════════════
        EasfBlock GetSelectedBlock()
        {
            TreeNode node = tvBlocks.SelectedNode;
            if (node == null || node.Tag == null || node.Tag.ToString() == "__root__")
                return null;
            int idx;
            if (!int.TryParse(node.Tag.ToString(), out idx)) return null;
            if (idx < 0 || idx >= blocks.Count) return null;
            return blocks[idx];
        }

        void MarkDirty()
        {
            dirty = true;
        }

        void SetStatus(string msg)
        {
            lblStatus.Text = msg;
        }

        bool ConfirmDiscardChanges()
        {
            return MessageBox.Show(
                "File đã có thay đổi chưa lưu. Tiếp tục sẽ mất dữ liệu. Bạn có muốn tiếp tục?",
                "Chưa lưu", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes;
        }

        // ════════════════════════════════════════════════════════════════
        //  CRYPTO & FORMAT HELPERS
        // ════════════════════════════════════════════════════════════════
        static byte[] AesDecrypt(byte[] cipher, byte[] key, byte[] iv)
        {
            try
            {
                using (RijndaelManaged aes = new RijndaelManaged())
                {
                    aes.KeySize = 128; aes.BlockSize = 128;
                    aes.Mode    = CipherMode.CBC;
                    aes.Padding = PaddingMode.None;
                    aes.Key = key; aes.IV = iv;
                    using (ICryptoTransform dec = aes.CreateDecryptor())
                    using (MemoryStream ms = new MemoryStream(cipher))
                    using (CryptoStream cs = new CryptoStream(ms, dec, CryptoStreamMode.Read))
                    using (MemoryStream out_ = new MemoryStream())
                    { cs.CopyTo(out_); return out_.ToArray(); }
                }
            }
            catch { return null; }
        }

        static byte[] AesEncrypt(byte[] plain, byte[] key, byte[] iv)
        {
            int padded = ((plain.Length + 15) / 16) * 16;
            byte[] input = new byte[padded];
            Buffer.BlockCopy(plain, 0, input, 0, plain.Length);
            using (RijndaelManaged aes = new RijndaelManaged())
            {
                aes.KeySize = 128; aes.BlockSize = 128;
                aes.Mode    = CipherMode.CBC;
                aes.Padding = PaddingMode.None;
                aes.Key = key; aes.IV = iv;
                using (ICryptoTransform enc = aes.CreateEncryptor())
                using (MemoryStream ms = new MemoryStream())
                using (CryptoStream cs = new CryptoStream(ms, enc, CryptoStreamMode.Write))
                { cs.Write(input, 0, input.Length); cs.FlushFinalBlock(); return ms.ToArray(); }
            }
        }

        static byte[] CreateEasfBlock(byte[] plain, byte[] key)
        {
            int digestLen   = Math.Min(32, plain.Length);
            byte[] digest   = ComputeSHA256(plain, 0, digestLen);
            byte[] encData  = AesEncrypt(plain, key, key);
            byte[] block    = new byte[EASF_HEADER_SIZE + encData.Length];
            int p = 0;
            block[p++] = 0x45; block[p++] = 0x41; block[p++] = 0x53; block[p++] = 0x46;
            uint sz = (uint)plain.Length;
            block[p++] = (byte)(sz >> 24); block[p++] = (byte)(sz >> 16);
            block[p++] = (byte)(sz >> 8);  block[p++] = (byte)sz;
            Buffer.BlockCopy(Encoding.ASCII.GetBytes("datax   "), 0, block, p, 8); p += 8;
            Buffer.BlockCopy(digest, 0, block, p, 32); p += 32;
            Buffer.BlockCopy(encData, 0, block, p, encData.Length);
            return block;
        }

        static byte[] ComputeSHA256(byte[] data, int offset, int count)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] seg = new byte[count];
                Buffer.BlockCopy(data, offset, seg, 0, count);
                return sha.ComputeHash(seg);
            }
        }

        static uint ReadUInt32LE(byte[] b, int o)
        {
            return (uint)(b[o] | (b[o+1]<<8) | (b[o+2]<<16) | (b[o+3]<<24));
        }

        static uint ReadUInt32BE(byte[] b, int o)
        {
            return (uint)((b[o]<<24) | (b[o+1]<<16) | (b[o+2]<<8) | b[o+3]);
        }

        static int AlignUp(int v, int a) { return (v + a - 1) & ~(a - 1); }

        static bool BytesEqual(byte[] a, byte[] b)
        {
            if (a == null || b == null || a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static int FindNextEASF(byte[] buf, int from)
        {
            for (int i = from; i <= buf.Length - 4; i++)
                if (buf[i]==0x45 && buf[i+1]==0x41 && buf[i+2]==0x53 && buf[i+3]==0x46)
                    return i;
            return -1;
        }
    }

    // ════════════════════════════════════════════════════════════════════════
    //  ENTRY POINT
    // ════════════════════════════════════════════════════════════════════════
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
