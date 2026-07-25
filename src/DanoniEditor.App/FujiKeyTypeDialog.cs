using System.Windows;
using System.Windows.Controls;
using DanoniEditor.Core.Import;
using DanoniEditor.Core.Models;

namespace DanoniEditor.App;

/// <summary>難易度名の決め方(2026-08-05)</summary>
internal enum FujiDifficultyNameMode { SetLater, SetNow, FromDifData }

/// <summary>難易度名選択の結果。Name/InitialSpeedはSetLater時は空文字/null。</summary>
internal readonly record struct FujiDifficultyNameChoice(FujiDifficultyNameMode Mode, string Name, double? InitialSpeed);

/// <summary>FujiImportSetupDialog.Askの結果一式。</summary>
internal readonly record struct FujiImportSetupResult(string KeyTypeId, FujiDifficultyNameChoice NameChoice);

/// <summary>
/// FUJIインポート時、キー種と難易度名を1つのウィンドウでまとめて決めるダイアログ
/// (2026-08-05再設計、ユーザー確定仕様「1回で済ませたい」)。
/// - キー種: difDataからは完全に無視する(difData[0]の表記は一切参照しない)。
///   テンプレートフォルダに存在するキー種一覧からユーザーが選んだ値のみを取り込み処理に用いる。
/// - 難易度名: difDataの全行(difData[1])をキー種欄の値に関わらず候補として拾い、
///   「後で設定する」(空のまま取り込み)・「今設定する」(選択中のみ現れる入力欄に手入力)と
///   並べて選ばせる(2026-08-06: キー種による絞り込みは廃止)。
/// </summary>
internal static class FujiImportSetupDialog
{
    private sealed record NameItem(string Label, FujiDifficultyNameMode Mode, DifDataCandidate? Candidate);

    /// <summary>allDifDataCandidatesは全キー種混在のまま(FujiImporter.ScanDifDataの結果そのまま)渡す。
    /// キー種選択に応じてこちら側でフィルタする。キャンセル時はnull。</summary>
    public static FujiImportSetupResult? Ask(Window owner, TemplateRepository templates,
        IReadOnlyList<DifDataCandidate> allDifDataCandidates, string fileName)
    {
        var keyTypeIds = templates.ListKeyTypeIds().ToList();

        var win = new Window
        {
            Title = $"FUJIインポート設定 - {fileName}",
            Owner = owner,
            Width = 400,
            Height = 320,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            WindowStyle = WindowStyle.ToolWindow,
        };

        var panel = new StackPanel { Margin = new Thickness(12) };
        panel.Children.Add(new TextBlock
        {
            Text = $"インポート中のファイル: {fileName}",
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        });

        // --- キー種 ---
        panel.Children.Add(new TextBlock { Text = "キー種", Margin = new Thickness(0, 0, 0, 4) });
        var keyTypeCombo = new ComboBox { ItemsSource = keyTypeIds, Margin = new Thickness(0, 0, 0, 12) };
        keyTypeCombo.SelectedItem = keyTypeIds.Contains("5") ? "5" : keyTypeIds.FirstOrDefault();
        panel.Children.Add(keyTypeCombo);

        // --- 難易度名 ---
        panel.Children.Add(new TextBlock { Text = "難易度名", Margin = new Thickness(0, 0, 0, 4) });
        var nameCombo = new ComboBox { DisplayMemberPath = nameof(NameItem.Label), Margin = new Thickness(0, 0, 0, 4) };
        panel.Children.Add(nameCombo);

        var nameBox = new TextBox { Margin = new Thickness(0, 0, 0, 8), Visibility = Visibility.Collapsed };
        panel.Children.Add(nameBox);

        void RefreshNameItems()
        {
            // 2026-08-06: キー種(difData[0])は取り込み処理では完全に無視する(ダイアログで選んだ値のみ使用)。
            // 難易度名(difData[1])はキー種欄の表記に関わらず全行を候補として拾う。
            var items = new List<NameItem>
            {
                new("後で設定する", FujiDifficultyNameMode.SetLater, null),
                new("今設定する", FujiDifficultyNameMode.SetNow, null),
            };
            items.AddRange(allDifDataCandidates.Select(c => new NameItem(c.DifficultyName, FujiDifficultyNameMode.FromDifData, c)));
            nameCombo.ItemsSource = items;
            // difData候補があれば先頭候補(=従来の暫定選択と同じ)を既定選択、無ければ「後で設定する」を既定にする。
            nameCombo.SelectedIndex = allDifDataCandidates.Count > 0 ? 2 : 0;
        }

        void RefreshNameBoxVisibility()
        {
            bool setNow = nameCombo.SelectedItem is NameItem { Mode: FujiDifficultyNameMode.SetNow };
            nameBox.Visibility = setNow ? Visibility.Visible : Visibility.Collapsed;
        }

        nameCombo.SelectionChanged += (_, _) => RefreshNameBoxVisibility();
        RefreshNameItems();
        RefreshNameBoxVisibility();

        var errorText = new TextBlock { Foreground = System.Windows.Media.Brushes.Red, Margin = new Thickness(0, 4, 0, 8), TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(errorText);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var ok = new Button { Content = "OK", Width = 70, Margin = new Thickness(0, 0, 8, 0), IsDefault = true };
        var cancel = new Button { Content = "キャンセル", Width = 70, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);

        win.Content = panel;

        FujiImportSetupResult? result = null;
        ok.Click += (_, _) =>
        {
            if (keyTypeCombo.SelectedItem is not string keyTypeId || string.IsNullOrWhiteSpace(keyTypeId))
            {
                errorText.Text = "キー種を選択してくださいませ。";
                return;
            }
            if (nameCombo.SelectedItem is not NameItem item)
            {
                errorText.Text = "難易度名を選択してくださいませ。";
                return;
            }

            FujiDifficultyNameChoice nameChoice;
            switch (item.Mode)
            {
                case FujiDifficultyNameMode.SetLater:
                    nameChoice = new FujiDifficultyNameChoice(item.Mode, "", null);
                    break;
                case FujiDifficultyNameMode.SetNow:
                    if (string.IsNullOrWhiteSpace(nameBox.Text))
                    {
                        errorText.Text = "難易度名を入力してくださいませ。";
                        return;
                    }
                    nameChoice = new FujiDifficultyNameChoice(item.Mode, nameBox.Text.Trim(), null);
                    break;
                default:
                    nameChoice = new FujiDifficultyNameChoice(item.Mode, item.Candidate!.DifficultyName, item.Candidate.InitialSpeed);
                    break;
            }

            result = new FujiImportSetupResult(keyTypeId, nameChoice);
            win.DialogResult = true;
        };
        cancel.Click += (_, _) => win.DialogResult = false;

        win.ShowDialog();
        return result;
    }
}
