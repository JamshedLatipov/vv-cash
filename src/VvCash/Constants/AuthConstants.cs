namespace VvCash.Constants;

public static class AuthConstants
{
    // NOT a normal expiry a cashier is ever expected to hit — closing a shift
    // already ends its authenticated session outright (see
    // PosViewModel.DoCloseShiftAsync, which wipes AuthToken/AuthTokenExpiresAt on
    // a successful close), and a shift is expected to be closed well within this
    // many hours. This constant is purely an upper bound: a backstop against a
    // register that never gets its shift closed — crash, power loss, a forgotten
    // till — staying auto-authenticated indefinitely. See AuthService.LoginAsync
    // for how it's combined with "remember me".
    public const int MaxShiftHours = 24;
}
