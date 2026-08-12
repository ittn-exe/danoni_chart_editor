using System.Text;
using DanoniEditor.Core.Export;

namespace DanoniEditor.Core.Tests.Export;

/// <summary>dos.txtエクスポート時の文字コード選択(2026-08-08要望対応)、DosTextEncodingの単体テスト。</summary>
public class DosTextEncodingTests
{
    [Fact]
    public void FindUnmappableChars_AsciiAndJapanese_ReturnsEmpty()
    {
        // 半角英数字・一般的な日本語(ひらがな・カタカナ・常用漢字)はShift-JISで表現できる
        var found = DosTextEncoding.FindUnmappableChars("Test曲名 テスト 日本語のタイトル123");
        Assert.Empty(found);
    }

    [Fact]
    public void FindUnmappableChars_Emoji_DetectsIt()
    {
        // 絵文字(サロゲートペア)はShift-JISに存在しないコードポイント
        var found = DosTextEncoding.FindUnmappableChars("曲名🎵テスト");
        Assert.Contains("🎵", found);
    }

    [Fact]
    public void FindUnmappableChars_NoUnmappableChars_ReturnsEmptyArray()
    {
        Assert.Empty(DosTextEncoding.FindUnmappableChars(""));
        Assert.Empty(DosTextEncoding.FindUnmappableChars("plain ascii"));
    }

    [Fact]
    public void FindUnmappableChars_DuplicateUnmappableChar_ReturnsOnlyOnce()
    {
        var found = DosTextEncoding.FindUnmappableChars("🎵🎵🎵");
        Assert.Single(found);
        Assert.Equal("🎵", found[0]);
    }

    [Fact]
    public void EncodeShiftJisWithReplacement_JapaneseText_RoundTripsCorrectly()
    {
        var encoding = Encoding.GetEncoding(932); // 提供元(CodePagesEncodingProvider)は内部で既に登録済みのはず
        string original = "テスト楽曲";
        byte[] bytes = DosTextEncoding.EncodeShiftJisWithReplacement(original);
        string decoded = encoding.GetString(bytes);
        Assert.Equal(original, decoded);
    }

    [Fact]
    public void EncodeShiftJisWithReplacement_Emoji_ReplacesWithQuestionMark()
    {
        // 絵文字はUTF-16のサロゲートペア(上位・下位2つのchar)で構成されており、Shift-JISのような
        // レガシーコードページはUTF-16コード単位ごとに変換不可を判定するため、置換後は「?」が
        // 2つ(ペア分)になる。FindUnmappableCharsの方は1文字(1絵文字)として数える(見た目の一致)。
        byte[] bytes = DosTextEncoding.EncodeShiftJisWithReplacement("曲🎵名");
        var encoding = Encoding.GetEncoding(932);
        string decoded = encoding.GetString(bytes);
        Assert.Equal("曲??名", decoded);
    }

    [Fact]
    public void EncodeShiftJisWithReplacement_ProducesNoBom()
    {
        // Shift-JISにBOMという概念自体が無いことの確認(先頭バイトが日本語文字の1バイト目であること)
        byte[] bytes = DosTextEncoding.EncodeShiftJisWithReplacement("あ");
        Assert.Equal(2, bytes.Length); // "あ"はShift-JISで2バイト
    }

    // =====================================================================
    // ReadAutoDetectText(2026-08-09要望対応: dos.txt/FUJIインポート時の文字コード自動判定)。
    // 第三者報告(dos.txtインポート時の文字化けでプロジェクトの一部フィールドが不可逆に破損)を受け、
    // ダイアログでユーザーに確認させることなく「UTF-8として厳密デコードを試み、失敗したら
    // Shift-JISへフォールバック」する方式で判定する。
    // =====================================================================

    [Fact]
    public void ReadAutoDetectText_Utf8Text_DecodesAsUtf8()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("|title=テスト曲名|artist=作者|");
        var (text, wasShiftJis) = DosTextEncoding.ReadAutoDetectText(bytes);
        Assert.Equal("|title=テスト曲名|artist=作者|", text);
        Assert.False(wasShiftJis);
    }

    [Fact]
    public void ReadAutoDetectText_Utf8WithBom_StripsBomAndDecodesAsUtf8()
    {
        byte[] bom = [0xEF, 0xBB, 0xBF];
        byte[] bytes = [.. bom, .. Encoding.UTF8.GetBytes("|title=テスト|")];
        var (text, wasShiftJis) = DosTextEncoding.ReadAutoDetectText(bytes);
        Assert.Equal("|title=テスト|", text);
        Assert.False(wasShiftJis);
        Assert.DoesNotContain('﻿', text);
    }

    [Fact]
    public void ReadAutoDetectText_ShiftJisText_FallsBackToShiftJisAndDetectsIt()
    {
        var sjis = Encoding.GetEncoding(932);
        byte[] bytes = sjis.GetBytes("|title=譜面提供祭|artist=作者|");
        var (text, wasShiftJis) = DosTextEncoding.ReadAutoDetectText(bytes);
        Assert.Equal("|title=譜面提供祭|artist=作者|", text);
        Assert.True(wasShiftJis);
    }

    [Fact]
    public void ReadAutoDetectText_ReportedMojibakeCase_DecodesCorrectlyAsShiftJis()
    {
        // 実際に報告された不具合(dos.txt由来のtuning欄が文字化けし不可逆に破損した事例)の再現データ。
        // 「譜面提供祭」をShift-JISで書き出したバイト列が、従来はUTF-8として誤読され
        // "���ʒ\U0004b7cd�"のような文字化けになっていた。
        var sjis = Encoding.GetEncoding(932);
        byte[] bytes = sjis.GetBytes("from 譜面提供祭");
        var (text, wasShiftJis) = DosTextEncoding.ReadAutoDetectText(bytes);
        Assert.Equal("from 譜面提供祭", text);
        Assert.True(wasShiftJis);
    }

    [Fact]
    public void ReadAutoDetectText_AsciiOnly_DecodesAsUtf8()
    {
        // ASCII文字のみのバイト列はUTF-8としてもShift-JISとしても同一結果になるが、
        // UTF-8として妥当なため厳密デコード側(wasShiftJis=false)で判定されることを確認する。
        byte[] bytes = Encoding.ASCII.GetBytes("|version=1|frame=0|");
        var (text, wasShiftJis) = DosTextEncoding.ReadAutoDetectText(bytes);
        Assert.Equal("|version=1|frame=0|", text);
        Assert.False(wasShiftJis);
    }
}
