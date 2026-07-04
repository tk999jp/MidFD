using System;
using System.Drawing;
using System.Windows.Forms;

namespace MidFD.Services;

public enum StatusKind
{
    Normal,
    Result,
    Error
}

/// <summary>
/// 通知およびステータス表示を管理するサービス。
/// MainForm の表示領域とタイマー制御をカプセル化する。
/// </summary>
public class NotificationService
{
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly System.Windows.Forms.Timer _messageTimer;
    private readonly Func<StatusKind, Color> _colorResolver;

    /// <summary>
    /// 一時メッセージ（Show）が表示された後、自動的に戻るデフォルトのメッセージ。
    /// 初期値は "Ready."
    /// </summary>
    public string DefaultMessage { get; set; } = "Ready.";

    /// <summary>
    /// デフォルトメッセージの種別。デフォルトに戻った際の色適用に使用する。
    /// </summary>
    public StatusKind DefaultMessageKind { get; set; } = StatusKind.Normal;

    /// <summary>
    /// 現在表示中のメッセージの種別。
    /// </summary>
    public StatusKind ActiveMessageKind { get; private set; } = StatusKind.Normal;

    public bool IsTemporaryMessageActive { get; private set; }

    public NotificationService(ToolStripStatusLabel statusLabel, System.Windows.Forms.Timer messageTimer, Func<StatusKind, Color> colorResolver)
    {
        _statusLabel = statusLabel ?? throw new ArgumentNullException(nameof(statusLabel));
        _messageTimer = messageTimer ?? throw new ArgumentNullException(nameof(messageTimer));
        _colorResolver = colorResolver ?? throw new ArgumentNullException(nameof(colorResolver));

        // タイマーのイベントをサービス側で処理する
        _messageTimer.Tick += (s, e) =>
        {
            _messageTimer.Stop();
            IsTemporaryMessageActive = false;
            _statusLabel.Text = DefaultMessage;
            ActiveMessageKind = DefaultMessageKind;
            _statusLabel.ForeColor = _colorResolver(DefaultMessageKind);
        };
    }

    /// <summary>
    /// 指定されたメッセージをステータスバーに表示し、一定時間後にクリアする。
    /// </summary>
    /// <param name="message">表示するメッセージ</param>
    /// <param name="kind">表示色の種別</param>
    public void Show(string message, StatusKind kind = StatusKind.Normal)
    {
        IsTemporaryMessageActive = true;
        ActiveMessageKind = kind;
        _statusLabel.Text = message;
        _statusLabel.ForeColor = _colorResolver(kind);
        _messageTimer.Stop();
        _messageTimer.Start(); // 既存設定（通常10秒）で開始
    }

    public void SetDefaultMessage(string message, StatusKind kind = StatusKind.Normal, bool applyToVisibleMessage = false)
    {
        DefaultMessage = message;
        DefaultMessageKind = kind;
        if (!applyToVisibleMessage)
        {
            return;
        }

        IsTemporaryMessageActive = false;
        ActiveMessageKind = kind;
        _statusLabel.Text = message;
        _statusLabel.ForeColor = _colorResolver(kind);
        _messageTimer.Stop();
    }

    /// <summary>
    /// ステータスバーにメッセージを表示し、自動リセットタイマーを停止する（永続表示用）。
    /// また、DefaultMessage もこのメッセージで上書きし、以降の Show() 後の復帰先とする。
    /// </summary>
    /// <param name="message">表示するメッセージ</param>
    /// <param name="kind">表示色の種別</param>
    public void SetPersistent(string message, StatusKind kind = StatusKind.Normal)
    {
        SetDefaultMessage(message, kind, applyToVisibleMessage: true);
    }

    /// <summary>
    /// 現在の配色設定変更をステータスラベルに強制再反映する。
    /// </summary>
    public void ApplyCurrentColors()
    {
        _statusLabel.ForeColor = _colorResolver(ActiveMessageKind);
    }

    /// <summary>
    /// 現在の配色設定変更をステータスラベルに強制再反映する。
    /// </summary>
    public void ApplyCurrentColors(StatusKind currentActiveKind)
    {
        ActiveMessageKind = currentActiveKind;
        _statusLabel.ForeColor = _colorResolver(currentActiveKind);
    }
}
