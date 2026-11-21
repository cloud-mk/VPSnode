using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;
using QRCoder;
using VPSnode.Core;
using Forms = System.Windows.Forms;
using Validation = VPSnode.Core.Validation;

namespace VPSnode.Desktop;

public partial class MainWindow
{
    private readonly record struct FieldSpec(string Key, string Label, string Value, int Height = 0);

    private Window Dialog(string title, UIElement content, double width, double height)
    {
        var scroller = new ScrollViewer { Content = content, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root = new Border { Child = scroller, Padding = new Thickness(14) };
        Ui.Brush(root, Border.BackgroundProperty, "Canvas");
        return new Window
        {
            Owner = this, Title = title, Width = Math.Min(width, SystemParameters.WorkArea.Width - 16),
            Height = Math.Min(height, SystemParameters.WorkArea.Height - 16),
            MinWidth = Math.Min(470, width), MinHeight = 220, Content = root, ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            FontFamily = FontFamily, FontSize = 14
        };
    }

    private Dictionary<string, string>? Form(string title, string intro, params FieldSpec[] fields) =>
        ValidatedForm(title, intro, null, fields);

    private Dictionary<string, string>? ValidatedForm(string title, string intro,
        Action<Dictionary<string, string>>? validate, params FieldSpec[] fields)
    {
        var list = Ui.Stack(Ui.Section(title));
        if (intro.Length > 0)
        {
            var note = Ui.Text(intro, brush: "Quiet");
            note.Margin = new Thickness(0, 0, 0, 12);
            list.Children.Add(note);
        }
        Dictionary<string, TextBox> inputs = [];
        var password = new PasswordBox { Padding = new Thickness(6, 4, 6, 4) };
        System.Windows.Automation.AutomationProperties.SetName(password, "SSH 密码");
        var clearPassword = new CheckBox { Content = "清除已存密码，改用密钥或 SSH 配置" };
        foreach (var f in fields)
        {
            var input = new TextBox
            {
                Text = f.Value, Margin = new Thickness(0, 4, 0, 9), TextWrapping = TextWrapping.Wrap,
                VerticalScrollBarVisibility = f.Height > 0 ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
                AcceptsReturn = f.Height > 0, MinHeight = f.Height
            };
            System.Windows.Automation.AutomationProperties.SetName(input, f.Label);
            inputs[f.Key] = input;
            list.Children.Add(Ui.Text(f.Label, brush: "Quiet", bold: true));
            if (f.Key == "password")
            {
                list.Children.Add(password);
                if (f.Value.Length > 0) list.Children.Add(clearPassword);
            }
            else if (f.Key == "key")
            {
                var keyRow = new DockPanel();
                var browse = Ui.Button("选择文件…", () =>
                {
                    var picker = new OpenFileDialog { Title = "选择 SSH 私钥", CheckFileExists = true };
                    if (picker.ShowDialog() == true) input.Text = picker.FileName;
                });
                DockPanel.SetDock(browse, Dock.Right);
                keyRow.Children.Add(browse);
                keyRow.Children.Add(input);
                list.Children.Add(keyRow);
            }
            else list.Children.Add(input);
        }
        var dialog = Dialog(title, list, 680, Math.Min(730, 170 + fields.Length * 63));
        var error = Ui.Text("", brush: "Bad");
        list.Children.Add(error);
        var controls = Ui.Row(Ui.Button("保存", () =>
        {
            try
            {
                validate?.Invoke(inputs.ToDictionary(x => x.Key, x => x.Value.Text.Trim()));
                dialog.DialogResult = true;
            }
            catch (InvalidDataException ex) { error.Text = ex.Message; }
        }, true),
            Ui.Button("取消", () => dialog.DialogResult = false));
        ((Button)controls.Children[0]).IsDefault = true;
        ((Button)controls.Children[1]).IsCancel = true;
        dialog.Loaded += (_, _) => inputs.Values.FirstOrDefault()?.Focus();
        list.Children.Add(controls);
        if (dialog.ShowDialog() != true) return null;
        var values = inputs.ToDictionary(x => x.Key, x => x.Value.Text.Trim());
        if (values.ContainsKey("password"))
        {
            if (clearPassword.IsChecked == true) values["password"] = "";
            else if (password.Password.Length > 0)
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(password.Password);
                try { values["password"] = Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(bytes, null,
                    System.Security.Cryptography.DataProtectionScope.CurrentUser)); }
                finally { System.Security.Cryptography.CryptographicOperations.ZeroMemory(bytes); password.Clear(); }
            }
        }
        return values;
    }

    private static int Integer(string input, int minimum, int maximum, string field) =>
        int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) &&
        value >= minimum && value <= maximum ? value : throw new InvalidDataException($"{field}须为 {minimum} 至 {maximum} 的整数。");

    private static string Id() => Guid.NewGuid().ToString("N");

    private void AddHost()
    {
        Host ReadHost(Dictionary<string, string> values)
        {
            if (!Validation.Address(values["addr"])) throw new InvalidDataException("请填写服务器 IP 或域名。");
            var host = new Host { Id = Id(), Name = Field(values["name"], values["addr"]), Address = values["addr"],
                User = values["user"], SshPort = Integer(values["port"], 1, 65535, "SSH 端口"), PasswordCipher = values["password"], KeyPath = values["key"] };
            Validation.Host(host);
            return host;
        }
        var f = ValidatedForm("通过地址添加 VPS", "填写平时 SSH 登录所用的地址和账号。保存后会自动读取主机、面板和入站信息。已有 SSH 别名可在 VPS 页直接导入。",
            values => ReadHost(values), new("addr", "服务器 IP 或域名（必填）", ""), new("user", "SSH 登录账号（留空沿用 OpenSSH 配置）", ""),
            new("port", "SSH 端口", "22"), new("key", "私钥文件路径（已有默认密钥可留空）", ""),
            new("password", "SSH 密码（选填，填写后使用密码认证）", ""), new("name", "显示名称（选填，默认用地址）", ""));
        if (f is null) return;
        try
        {
            var host = ReadHost(f);
            _store.Save(host);
            OpenHost(host.Id);
            StartInventory(host.Id);
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void EditHost(Host old)
    {
        var host = old;
        var f = Form("编辑 VPS", "使用已有 SSH 别名时沿用本机 OpenSSH 配置。使用地址时沿用此处填写的账号、端口和私钥。",
            new("name", "显示名称", host.Name), new("addr", "公网 IPv4、域名或主要连接地址", host.Address),
            new("v6", "IPv6（可选）", host.Ipv6), new("alias", "Windows OpenSSH 别名（可选）", host.SshAlias),
            new("user", "登录账号", host.User), new("port", "SSH 端口", host.SshPort.ToString()),
            new("password", "SSH 密码（留空保留）", host.PasswordCipher), new("key", "本机 SSH 私钥文件路径", host.KeyPath), new("hostname", "主机名", host.Hostname),
            new("hardware", "硬件规格", host.Hardware), new("provider", "云厂商或地区", host.Provider),
            new("tunnel", "3x-ui 本机隧道端口（可选）", host.TunnelPort?.ToString() ?? ""),
            new("panel", "面板路径覆盖（选填，默认使用自动采集值）", host.PanelPath), new("notes", "备注", host.Notes, 65));
        if (f is null) return;
        try
        {
            _store.Save(host with
            {
                Name = f["name"], Address = f["addr"], Ipv6 = f["v6"], SshAlias = f["alias"], User = f["user"],
                SshPort = Integer(f["port"], 1, 65535, "SSH 端口"), PasswordCipher = f["password"], KeyPath = f["key"], Hostname = f["hostname"],
                Hardware = f["hardware"], Provider = f["provider"], PanelPath = f["panel"], Notes = f["notes"],
                TunnelPort = f["tunnel"].Length == 0 ? null : Integer(f["tunnel"], 1, 65535, "隧道端口")
            }, old);
            OpenHost(host.Id);
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void StartInventory(string id)
    {
        if (_current is null) Run("inventory", id);
        else { _store.Queue("inventory", id); Status("已保存，盘点已排入队列"); }
    }

    private async void ImportAlias()
    {
        try
        {
            var config = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
            var picker = new ComboBox { IsEditable = true, ItemsSource = SshInventory.KnownAliases(config),
                Margin = new Thickness(0, 6, 0, 14), Padding = new Thickness(6) };
            var box = Ui.Stack(Ui.Section("选择 SSH 别名"), Ui.Text("选择本机已有的 SSH 连接，或输入其他已配置的别名。保存后自动盘点主机和面板。", brush: "Quiet"), picker);
            var dialog = Dialog("从 SSH 别名导入", box, 540, 280);
            var save = Ui.Button("导入并盘点", () => dialog.DialogResult = true, true);
            save.IsDefault = true;
            var cancel = Ui.Button("取消", () => dialog.DialogResult = false);
            cancel.IsCancel = true;
            box.Children.Add(Ui.Row(save, cancel));
            if (dialog.ShowDialog() != true) return;
            var candidate = await SshInventory.FromAlias(picker.Text.Trim(), _app.Token);
            var result = await Task.Run(() => _monitor.Maintain(() => _store.ImportSshAlias(candidate), _app.Token));
            candidate = result.Host;
            if (result.Summary.Conflicts.Length > 0) throw new InvalidDataException(result.Summary.Conflicts[0]);
            OpenHost(candidate.Id);
            StartInventory(candidate.Id);
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void ImportInventory()
    {
        var picker = new OpenFileDialog { Filter = "VPSnode 数据 (*.json)|*.json", Multiselect = false };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(picker.FileName).Length > 4 * 1024 * 1024) throw new InvalidDataException("导入文件过大。");
            var assets = Wire.Decode<InventoryDocument>(File.ReadAllText(picker.FileName));
            var preview = _store.Import(assets, false);
            var text = $"新增 {preview.Added} 项，更新 {preview.Updated} 项，冲突 {preview.Conflicts.Length} 项。";
            if (preview.Conflicts.Length > 0) text += Environment.NewLine + string.Join(Environment.NewLine, preview.Conflicts.Take(8));
            if (MessageBox.Show(this, text, "导入预览", MessageBoxButton.OKCancel) == MessageBoxResult.OK)
            {
                _store.Import(assets, true);
                Render();
            }
        }
        catch (Exception ex) { Fail(ex); }
    }

    private ChoiceHost HostChoice(string selected)
    {
        var hosts = new[] { new Host { Id = "", Name = "未归属" } }.Concat(_store.Hosts.OrderBy(x => x.Name)).ToArray();
        var labels = hosts.Select(h => (object)(h.Address.Length > 0 ? $"{h.Name} · {h.Address}" : h.Name)).ToArray();
        var index = Math.Max(0, Array.FindIndex(hosts, h => h.Id == selected));
        var control = Choice(labels, labels[index], _ => { });
        return new(control, () => hosts[Math.Max(0, control.SelectedIndex)].Id);
    }

    private sealed record ChoiceHost(ComboBox Control, Func<string> Id);

    private void ImportLinks(bool fromFile)
    {
        string input = "";
        if (fromFile)
        {
            var picker = new OpenFileDialog { Filter = "TXT 文件 (*.txt)|*.txt|全部文件|*.*" };
            if (picker.ShowDialog(this) != true) return;
            try
            {
                if (new FileInfo(picker.FileName).Length > 2 * 1024 * 1024) { Status("文件过大"); return; }
                input = File.ReadAllText(picker.FileName);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Fail(ex); return; }
        }
        var host = HostChoice(_nodeHostFilter);
        var editor = new TextBox
        {
            Text = input, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, Height = 220,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 10, 0, 8)
        };
        var message = Ui.Text("", brush: "Quiet");
        var body = Ui.Stack(Ui.Section(fromFile ? "批量导入 TXT" : "粘贴分享链接"),
            Ui.Row(Ui.Text("所属 VPS  "), host.Control), Ui.Text("一行一个节点。", brush: "Quiet"), editor, message);
        var window = Dialog("导入代理节点", body, 720, 460);
        body.Children.Add(Ui.Row(Ui.Button("导入", () =>
        {
            List<Node> good = [];
            List<int> bad = [];
            var lines = editor.Text.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                var value = lines[i].Trim();
                if (value.Length == 0) continue;
                try { good.Add(ShareLink.New(value, host.Id())); }
                catch (Exception e) when (e is InvalidDataException or UriFormatException) { bad.Add(i + 1); }
            }
            if (good.Count == 0)
            {
                message.Text = "没有可导入的链接，请检查格式或协议。";
                return;
            }
            if (bad.Count > 0 && MessageBox.Show(window,
                $"第 {string.Join("、", bad.Take(12))} 行无法导入。保存其余 {good.Count} 条？", "导入预览",
                MessageBoxButton.OKCancel) != MessageBoxResult.OK) return;
            try
            {
                var outcome = _store.Import(new(1, [], [.. good], []), true);
                Status($"已导入 {outcome.Added} · 更新 {outcome.Updated}");
                window.DialogResult = true;
            }
            catch (Exception ex) { Fail(ex); }
        }, true), Ui.Button("取消", () => window.Close())));
        if (window.ShowDialog() == true) Go("nodes");
    }

    private void ExportNode(Node node)
    {
        var picker = new SaveFileDialog { Filter = "VPSnode 资产 JSON|*.json", FileName = "node-config.json" };
        if (picker.ShowDialog(this) != true) return;
        if (MessageBox.Show(this, "导出文件包含客户端凭据、原始服务端配置和关联主机资料（含密钥路径，不含 SSH 密码）。保存到所选位置？",
            "导出配置", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes) return;
        try
        {
            Host[] hosts = _store.Hosts.Where(h => h.Id == node.HostId)
                .Select(h => h with { PasswordCipher = "" }).ToArray();
            var exported = node;
            if (exported.Link.Length == 0)
            {
                try { exported = node with { Link = ProxyProbe.ClientLink(_store, node) }; }
                catch (InvalidDataException) { /* A server-only inventory is still exportable. */ }
            }
            File.WriteAllText(picker.FileName, Wire.Encode(new InventoryDocument(1, hosts, [exported], [])));
            Status("配置已导出");
        }
        catch (Exception ex) { Fail(ex); }
    }

    private void EditNode(Node n)
    {
        var host = HostChoice(n.HostId);
        var name = new TextBox { Text = n.Name };
        var link = new TextBox { TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Height = 66,
            ToolTip = "留空保留原链接，粘贴分享链接可更新客户端配置。" };
        var notes = new TextBox { Text = n.Notes, AcceptsReturn = true, Height = 55, TextWrapping = TextWrapping.Wrap };
        var mode = new ComboBox { ItemsSource = new[] { "auto", "ipv4", "ipv6" }, SelectedItem = n.IpMode };
        var pause = new CheckBox { Content = "手动暂停巡检", IsChecked = n.Paused };
        var server = new TextBox { TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, Height = 75,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            ToolTip = "小型 JSON 字典。留空保留已存字段。" };
        var related = new TextBox { Text = Wire.Encode(n.Related), TextWrapping = TextWrapping.Wrap, AcceptsReturn = true,
            Height = 75, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var body = Ui.Stack(Ui.Section("节点配置"),
            Ui.Text("名称"), name, Ui.Text("所属 VPS"), host.Control, Ui.Text("IP 测试模式"), mode,
            Ui.Text("替换客户端链接（留空保留）"), link, Ui.Text("服务端字段 JSON（留空保留）"), server,
            Ui.Text("回落及关联字段 JSON"), related, Ui.Text("备注"), notes, pause);
        var window = Dialog("编辑节点", body, 720, 730);
        body.Children.Add(Ui.Row(Ui.Button("保存", () =>
        {
            try
            {
                var serverFields = server.Text.Trim().Length == 0 ? n.Server :
                    Wire.Decode<Dictionary<string, string>>(server.Text.Trim());
                var relatedFields = related.Text.Trim().Length == 0 ? n.Related :
                    Wire.Decode<Dictionary<string, string>>(related.Text.Trim());
                var updated = ShareLink.Normalize(n with
                {
                    Name = name.Text.Trim(), HostId = host.Id(), Link = Field(link.Text.Trim(), n.Link),
                    Notes = notes.Text.Trim(), IpMode = (string?)mode.SelectedItem ?? "auto", Paused = pause.IsChecked == true,
                    Manual = true, SourceRemoved = false, Server = serverFields, Related = relatedFields
                });
                _store.Save(updated, n);
                window.DialogResult = true;
            }
            catch (Exception ex) { Fail(ex); }
        }, true), Ui.Button("取消", () => window.Close())));
        if (window.ShowDialog() == true) Render();
    }

    private void EditWebsite(WebTarget? current)
    {
        var site = current ?? new WebTarget { Id = Id(), Name = "" };
        var persisted = _store.Websites.FirstOrDefault(w => w.Id == site.Id);
        var host = HostChoice(site.HostId);
        var body = Ui.Stack();
        TextBox Input(StackPanel parent, string label, string value)
        {
            var box = new TextBox { Text = value, Margin = new Thickness(0, 3, 0, 8) };
            System.Windows.Automation.AutomationProperties.SetName(box, label);
            parent.Children.Add(Ui.Text(label, brush: "Quiet"));
            parent.Children.Add(box);
            return box;
        }

        var name = Input(body, "显示名称（选填）", site.Name);
        var domain = Input(body, "网站域名", site.Domain);
        body.Children.Add(Ui.Text("所属 VPS", brush: "Quiet"));
        body.Children.Add(host.Control);

        var manualFallbacks = _store.Websites.FirstOrDefault(w => w.Id == site.Id)?.FallbackNodeIds ?? [];
        if (site.AutoFallbackNodeIds.Length > 0)
            body.Children.Add(Ui.Text("配置回落（自动）：" + string.Join("、", _store.Nodes.Where(n => site.AutoFallbackNodeIds.Contains(n.Id)).Select(n => n.Name)) +
                "。自动关联随盘点更新，不会因编辑网站而变成手工登记。", brush: "Quiet"));
        var choices = _store.Nodes.OrderBy(n => n.Name).Select(n =>
            (Node: n, Box: new CheckBox { Content = $"{n.Name} · {n.Protocol} {n.Port}",
                IsChecked = manualFallbacks.Contains(n.Id) })).ToArray();
        var nodes = Ui.Stack();
        var selection = new Expander { Content = nodes, IsExpanded = manualFallbacks.Length > 0,
            Margin = new Thickness(0, 10, 0, 6) };
        void CountSelected() => selection.Header = $"手工登记回落（已选 {nodes.Children.OfType<CheckBox>().Count(c => c.IsChecked == true)}）";
        void RefreshChoices()
        {
            nodes.Children.Clear();
            foreach (var choice in choices.Where(x => host.Id().Length == 0 || x.Node.HostId == host.Id()))
                nodes.Children.Add(choice.Box);
            if (nodes.Children.Count == 0) nodes.Children.Add(Ui.Text("暂无可关联的节点。", brush: "Quiet"));
            CountSelected();
        }
        foreach (var choice in choices)
        {
            choice.Box.Checked += (_, _) => CountSelected();
            choice.Box.Unchecked += (_, _) => CountSelected();
        }
        host.Control.SelectionChanged += (_, _) => RefreshChoices();
        RefreshChoices();
        body.Children.Add(selection);

        var advanced = Ui.Stack();
        var port = Input(advanced, "证书连接端口", site.Port.ToString());
        var address = Input(advanced, "证书连接地址（可填源站 IP）", site.ConnectAddress);
        var sni = Input(advanced, "证书 SNI（留空使用域名）", site.Sni);
        var location = Input(advanced, "检测位置", site.Location);
        var url = Input(advanced, "额外要检测的 URL", site.Url);
        var keyword = Input(advanced, "额外 URL 应包含的文字", site.Contains);
        var expect = Input(advanced, "额外 URL 预期状态码", site.ExpectedStatus?.ToString() ?? "");
        body.Children.Add(new Expander { Header = "证书与自定义检测设置", Content = advanced,
            IsExpanded = site.Port != 443 || site.ConnectAddress.Length > 0 || site.Sni.Length > 0 || site.Url.Length > 0 });
        var error = Ui.Text("", brush: "Bad");
        body.Children.Add(error);
        var dialog = Dialog(current is null ? "添加网站或证书" : "编辑网站或证书", body, 640, 600);
        var save = Ui.Button("保存", () =>
        {
            try
            {
                var chosen = choices.Where(x => nodes.Children.Contains(x.Box) && x.Box.IsChecked == true);
                var updated = site with
                {
                    Name = Field(name.Text.Trim(), Field(domain.Text.Trim(), sni.Text.Trim())), Domain = domain.Text.Trim(),
                    HostId = host.Id(), FallbackNodeIds = chosen.Select(x => x.Node.Id).ToArray(), AutoFallbackNodeIds = [],
                    Port = Integer(port.Text.Trim(), 1, 65535, "证书端口"), ConnectAddress = address.Text.Trim(),
                    Sni = sni.Text.Trim(), Location = Field(location.Text.Trim(), "公网"), Url = url.Text.Trim(),
                    Contains = keyword.Text.Trim(), ExpectedStatus = expect.Text.Trim().Length == 0 ? null :
                        Integer(expect.Text.Trim(), 100, 599, "状态码")
                };
                if (persisted == null) _store.Save(updated);
                else _store.Save(updated, persisted);
                dialog.DialogResult = true;
            }
            catch (InvalidDataException ex) { error.Text = ex.Message; }
        }, true);
        var cancel = Ui.Button("取消", () => dialog.DialogResult = false);
        save.IsDefault = true;
        cancel.IsCancel = true;
        body.Children.Add(Ui.Row(save, cancel));
        if (dialog.ShowDialog() == true) Go("web");
    }

    private void Share(Node node)
    {
        string full;
        ShareLink link;
        try { full = ProxyProbe.ClientLink(_store, node); link = ShareLink.Parse(full); }
        catch (Exception ex) { Fail(ex); return; }

        var masked = $"{link.Scheme}://••••@{link.Address}:{(link.Port == 0 ? link.Option("mport") : link.Port.ToString())}?…";
        var text = new TextBox
        {
            Text = masked, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            FontFamily = new FontFamily("Consolas"), MaxHeight = 130,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 8, 0, 10)
        };
        bool shown = false;
        var panel = Ui.Stack(Ui.Section("分享节点"), Ui.Value(node.Name, 17, bold: true), text);
        panel.Children.Add(Ui.Row(Ui.Button("复制链接", () => { Copy(full); Status("已复制分享链接"); }, true),
            Ui.Button("显示 / 隐藏完整链接", () => { shown = !shown; text.Text = shown ? full : masked; })));
        panel.Children.Add(Ui.Text("链接和二维码包含节点凭据。", brush: "Quiet"));
        var qrPanel = Ui.Stack(Ui.Section("二维码"));
        BitmapSource? source = null;
        try
        {
            using var data = QRCodeGenerator.GenerateQrCode(full, QRCodeGenerator.ECCLevel.M);
            using var qr = new PngByteQRCode(data);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            using var bytes = new MemoryStream(qr.GetGraphic(12));
            image.StreamSource = bytes;
            image.EndInit();
            image.Freeze();
            source = image;
            qrPanel.Children.Add(QrImage(image, 400));
        }
        catch (Exception ex) when (ex is QRCoder.Exceptions.DataTooLongException or ArgumentException)
        {
            qrPanel.Children.Add(Ui.Text("链接长度超出二维码容量。可复制完整链接。", brush: "Warn"));
        }
        var dialog = Dialog("分享节点", Ui.Columns(Ui.Card(panel), Ui.Card(qrPanel)), 940, 630);
        if (source is not null) qrPanel.Children.Add(Ui.Button("全屏二维码", () => FullQr(dialog, source)));
        panel.Children.Add(Ui.Button("关闭", () => dialog.Close()));
        dialog.ShowDialog();
    }

    private static Viewbox QrImage(BitmapSource source, double side)
    {
        var image = new Image { Source = source, Width = side, Height = side, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        return new Viewbox
        {
            Child = image, Stretch = Stretch.Uniform, Width = side, Height = side,
            MaxWidth = side, MaxHeight = side, HorizontalAlignment = HorizontalAlignment.Center
        };
    }

    private static void FullQr(Window owner, BitmapSource source)
    {
        var image = new Image { Source = source, Stretch = Stretch.Uniform, Margin = new Thickness(28) };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        var window = new Window
        {
            Owner = owner, WindowState = WindowState.Maximized, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Background = Brushes.White,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var dock = new DockPanel();
        var close = new Button { Content = "关闭（Esc）", HorizontalAlignment = HorizontalAlignment.Center,
            Background = Brushes.White, Foreground = Brushes.Black, Margin = new Thickness(0, 12, 0, 8) };
        close.Click += (_, _) => window.Close();
        DockPanel.SetDock(close, Dock.Top);
        dock.Children.Add(close);
        dock.Children.Add(image);
        window.Content = dock;
        window.KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) window.Close(); };
        window.ShowDialog();
    }

    private void SyncDialog()
    {
        try
        {
            var s = _store.Settings;
            var groups = V2Sync.Groups(s.V2Directory);
            var choices = Ui.Stack(Ui.Section("v2rayN 同步"));
            var last = _store.Latest().GetValueOrDefault(("sync", "v2rayN"));
            choices.Children.Add(Ui.Text(last is null ? "尚未同步" : $"{Time(last)} · {last.Detail}", brush: "Quiet"));
            var rows = groups.Select(g => (g, select: new CheckBox { Content = $"{g.Name} · {g.Count} 个", IsChecked = true, MinWidth = 170 },
                follow: new CheckBox { Content = "自动跟随", IsChecked = s.FollowGroups.Contains(g.Id) })).ToArray();
            foreach (var item in rows) choices.Children.Add(Ui.Row(item.select, item.follow));
            var result = Ui.Text("", brush: "Quiet");
            choices.Children.Add(result);
            var window = Dialog("v2rayN 同步", choices, 570, 530);
            choices.Children.Add(Ui.Row(Ui.Button("立即同步选中分组", () =>
            {
                try
                {
                    var summary = _monitor.Sync(false, rows.Where(x => x.select.IsChecked == true).Select(x => x.g.Id).ToArray());
                    result.Text = $"新增 {summary.Added} · 更新 {summary.Updated} · 不支持 {summary.Unsupported}" +
                        (summary.Conflicts.Length == 0 ? "" : "\n" + string.Join("\n", summary.Conflicts.Take(10)));
                }
                catch (Exception ex) { Fail(ex); }
            }, true), Ui.Button("保存自动跟随", () =>
            {
                var follow = rows.Where(x => x.follow.IsChecked == true).Select(x => x.g.Id).ToArray();
                using var patch = System.Text.Json.JsonDocument.Parse(Wire.Encode(new { followGroups = follow }));
                _store.EditSettings(patch.RootElement);
                result.Text = follow.Length == 0 ? "自动跟随已关闭。" : $"已为 {follow.Length} 个分组开启自动跟随。";
            }), Ui.Button("关闭", () => window.Close())));
            window.ShowDialog();
            if (_page != "settings") Render();
        }
        catch (Exception ex) { Fail(ex); }
    }
}
