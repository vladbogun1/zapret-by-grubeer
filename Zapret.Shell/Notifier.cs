using Zapret.Core.AutoSelect;
using WinForms = System.Windows.Forms;

namespace Zapret.Shell;

/// <summary>
/// Decides when the product is allowed to interrupt.
/// <para>
/// The rule from the brief is that silence means success: no toast, no badge, no dialog while things work. Only
/// three things earn an interruption — it broke and could not be fixed, it broke and was fixed without you, and
/// an update wants a decision. Everything else the user can see by opening the window (docs/nextgen-ux.md §6).
/// </para>
/// </summary>
public sealed class Notifier(WinForms.NotifyIcon tray)
{
    private ProductStage _previous = ProductStage.FirstRun;
    private bool _announcedStuck;

    public bool Enabled { get; set; } = true;

    /// <summary>
    /// Called on every state change. Notifies only on a transition worth a person's attention, and never twice
    /// for the same situation.
    /// </summary>
    public void Observe(ProductState state)
    {
        var previous = _previous;
        _previous = state.Stage;

        if (!Enabled) return;

        switch (state.Stage)
        {
            // Fixed without being asked. Worth knowing precisely because the user did nothing.
            case ProductStage.Working when previous is ProductStage.Repairing or ProductStage.Degraded:
                _announcedStuck = false;
                Show(Text.Current["notify.repaired.title"], Text.Current["notify.repaired.body"], WinForms.ToolTipIcon.Info);
                break;

            // Could not be fixed. The only state that needs the user, so it says what to try.
            case ProductStage.Stuck when !_announcedStuck:
                _announcedStuck = true;
                Show(Text.Current["notify.stuck.title"],
                    state.AdviceKey is null ? Text.Current["notify.stuck.body"] : Text.Current[state.AdviceKey],
                    WinForms.ToolTipIcon.Warning);
                break;

            case ProductStage.Working or ProductStage.Off:
                _announcedStuck = false;
                break;
        }
    }

    /// <summary>An update is a decision, not an event, so it is announced once and never repeated.</summary>
    public void AnnounceUpdate(string version)
    {
        if (!Enabled) return;

        Show(Text.Current["notify.update.title"], Text.Current.Format("notify.update.body", version), WinForms.ToolTipIcon.Info);
    }

    /// <summary>
    /// A tray balloon rather than a WinRT toast: an unpackaged application needs a shortcut carrying an AUMID
    /// for toasts to appear at all, and a notification that silently fails is worse than a plain one that works.
    /// </summary>
    private void Show(string title, string body, WinForms.ToolTipIcon icon)
    {
        try
        {
            tray.ShowBalloonTip(9000, title, body, icon);
        }
        catch (Exception)
        {
            // Never let telling the user something become the thing that breaks.
        }
    }
}
