using System;
using System.Windows.Forms;

namespace MidFD.Services;

/// <summary>
/// 通知およびステータス表示を管理するサービス。
/// MainForm の表示領域とタイマー制御をカプセル化する。
/// </summary>
public class NotificationService
{
    private readonly ToolStripStatusLabel _statusLabel;
    private readonly System.Windows.Forms.Timer _messageTimer;

    /// <summary>
    /// 一時メッセージ（Show）が表示された後、自動的に戻るデフォルトのメッセージ。
    /// 初期値は "Ready."
    /// </summary>
    public string DefaultMessage { get; set; } = "Ready.";

    public NotificationService(ToolStripStatusLabel statusLabel, System.Windows.Forms.Timer messageTimer)
    {
        _statusLabel = statusLabel ?? throw new ArgumentNullException(nameof(statusLabel));
        _messageTimer = messageTimer ?? throw new ArgumentNullException(nameof(messageTimer));

        // タイマーのイベントをサービス側で処理する
        _messageTimer.Tick += (s, e) =>
        {
            _messageTimer.Stop();
            _statusLabel.Text = DefaultMessage;
        };
    }

    /// <summary>
    /// 指定されたメッセージをステータスバーに表示し、一定時間後にクリアする。
    /// </summary>
    /// <param name="message">表示するメッセージ</param>
    public void Show(string message)
    {
        _statusLabel.Text = message;
        _messageTimer.Stop();
        _messageTimer.Start(); // 既存設定（通常10秒）で開始
    }

    /// <summary>
    /// ステータスバーにメッセージを表示し、自動リセットタイマーを停止する（永続表示用）。
    /// また、DefaultMessage もこのメッセージで上書きし、以降の Show() 後の復帰先とする。
    /// </summary>
    public void SetPersistent(string message)
    {
        DefaultMessage = message;
        _statusLabel.Text = message;
        _messageTimer.Stop();
    }
}
