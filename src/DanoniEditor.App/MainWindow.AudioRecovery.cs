using System.IO;
using System.Windows.Threading;
using DanoniEditor.Core.Audio;
using DanoniEditor.Editing;

namespace DanoniEditor.App;

// 音源ファイルの欠落への対処(2026-10-06)。
// 旧バージョンはBASE64楽曲データのデコード結果をOSの一時フォルダへ書き出し、そのパスをプロジェクトへ
// 保存していた。OS側の掃除でファイルが消えると、プロジェクトを開いても黙って「未読込」になっていた。
public partial class MainWindow
{
    /// <summary>同一セッション内で、同じ欠落パスの通知を繰り返さないための記録
    /// (タブ切替のたびに同じ警告が出続けないようにする)。</summary>
    private readonly HashSet<string> _missingAudioNotified = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>記録されている音源パスのファイルが存在しない場合の対処。
    /// 旧バージョンが一時フォルダへ書き出したファイルなら、./temp とOS一時フォルダから同名ファイルを探し、
    /// 見つかれば./tempへ取り込んでプロジェクト内のパスを差し替える。見つからなければ通知する。
    /// 戻り値は読み込みに使うパス(救済できなければnull)。</summary>
    private string? TryRecoverMissingAudio(string missingPath)
    {
        var doc = _document;
        if (doc is null) return null;

        var fileName = Path.GetFileName(missingPath);
        if (Base64MusicDecoder.IsDecodedFileName(fileName))
        {
            foreach (var dir in new[] { AppPaths.TempDir, Path.GetTempPath() })
            {
                string candidate;
                try { candidate = Path.Combine(dir, fileName); }
                catch (ArgumentException) { continue; }
                if (!File.Exists(candidate)) continue;

                var finalPath = candidate;
                try
                {
                    var destination = Path.Combine(AppPaths.TempDir, fileName);
                    if (!string.Equals(Path.GetFullPath(candidate), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
                    {
                        Directory.CreateDirectory(AppPaths.TempDir);
                        if (!File.Exists(destination)) File.Copy(candidate, destination);
                        finalPath = destination;
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    AppLog.Write("TryRecoverMissingAudio copy", ex);
                    finalPath = candidate; // コピーできなくても、見つかった場所のファイルをそのまま使う
                }

                ReplaceAudioPath(doc, missingPath, finalPath);
                Notify(NotificationLevel.Info,
                    $"音源ファイル「{fileName}」を {Path.GetDirectoryName(finalPath)} から復元しました。プロジェクトを保存すると新しい場所が記録されます。");
                return finalPath;
            }

            NotifyMissingAudioOnce(missingPath,
                $"音源ファイルが見つかりません: {fileName}\n" +
                "BASE64楽曲データから読み込んだ一時ファイルが削除された可能性があります。" +
                "楽曲データ(.js)をもう一度ドロップして読み込み直してください。",
                doc);
            return null;
        }

        NotifyMissingAudioOnce(missingPath,
            $"音源ファイルが見つかりません: {missingPath}\nファイルを移動・削除していないか確認してください。",
            doc);
        return null;
    }

    private void NotifyMissingAudioOnce(string missingPath, string message, EditorDocument doc)
    {
        if (!_missingAudioNotified.Add(missingPath)) return;

        // 共通(1曲目)の音源パスなら、その場でファイルを選び直せるようにする
        bool isSharedSong = string.Equals(doc.Project.AudioFilePath, missingPath, StringComparison.OrdinalIgnoreCase);
        Notify(NotificationLevel.Warning, message, key: "audio-missing:" + missingPath,
            actionText: isSharedSong ? "音楽ファイルを選択..." : null,
            action: isSharedSong ? () => LoadAudio_Click(this, new System.Windows.RoutedEventArgs()) : null);
    }

    /// <summary>プロジェクト内で oldPath を指している音源パス(共通の音源・2曲目以降)をすべて newPath へ差し替える。
    /// 差し替えは未保存の変更として扱う(読み込み処理の途中で画面更新が走らないよう、通知は後回しにする)。</summary>
    private void ReplaceAudioPath(EditorDocument doc, string oldPath, string newPath)
    {
        var project = doc.Project;
        if (string.Equals(project.AudioFilePath, oldPath, StringComparison.OrdinalIgnoreCase))
            project.AudioFilePath = newPath;
        foreach (var song in project.AdditionalSongs)
            if (string.Equals(song.AudioFilePath, oldPath, StringComparison.OrdinalIgnoreCase))
                song.AudioFilePath = newPath;

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () => doc.NotifyChanged());
    }

    private void AudioPlayer_OpenFailed(string path, Exception ex)
    {
        // 失敗した音源が今の音源なら、「読み込み済み」の表示を元に戻す
        if (string.Equals(path, _currentLoadedAudioPath, StringComparison.OrdinalIgnoreCase))
        {
            AudioFileText.Text = "音楽未読込";
            AudioFileText.FontStyle = System.Windows.FontStyles.Italic;
            _audioLoaded = false;
            AudioTimeText.Text = "-";
        }
        Notify(NotificationLevel.Error,
            $"音楽ファイルを読み込めませんでした: {Path.GetFileName(path)}\n" +
            $"{ex.Message}\n形式が未対応か、ファイルが壊れている可能性があります。",
            key: "audio-open-failed:" + path);
    }

    private void AudioPlayer_OutputFailed(Exception ex)
    {
        Notify(NotificationLevel.Error,
            $"音声の出力を初期化できませんでした: {ex.Message}\n音は鳴りませんが、編集は続けられます。",
            key: "audio-output-failed");
    }
}
