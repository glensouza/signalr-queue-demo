namespace SignalRQueueDemo.Web.Services;

/// <summary>
/// Holds the staff key entered on the sign-in screen, in memory, for the lifetime of the Blazor circuit only —
/// registered <b>Scoped</b>, so it dies with the tab/circuit automatically and never needs an explicit
/// "sign out" to clear itself, the direct analogue of the Angular staff app's in-memory-only session signal (see
/// <c>StaffSessionService.ts</c> in <c>internal-queue</c>) but actually stronger: a page reload there could
/// theoretically resurrect a signal if the app were changed to persist it, where a Blazor circuit reload always
/// starts a fresh scope with no key.
///
/// <para>
/// Models the internal-vs-public trust boundary from ADR-0001, same as <c>StaffAuthFilter</c> on the REST side —
/// production replaces both with real authentication (Entra ID). This POC only needs to demonstrate that staff
/// pages are gated at all.
/// </para>
/// </summary>
public sealed class StaffSessionService
{
  /// <summary>The entered key, or null if not signed in.</summary>
  public string? StaffKey { get; private set; }

  public bool IsSignedIn => this.StaffKey is not null;

  /// <summary>Called once the entered key has already been verified against config — see the sign-in page.</summary>
  public void SignIn(string staffKey) => this.StaffKey = staffKey;

  public void SignOut() => this.StaffKey = null;
}
