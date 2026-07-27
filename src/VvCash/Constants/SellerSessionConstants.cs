using System;

namespace VvCash.Constants;

public static class SellerSessionConstants
{
    // How long the register may sit idle before the acting seller must be
    // re-confirmed with a PIN. SellerSession's own parameterless constructor
    // hardcodes this same value as a convenience for tests/manual usage, but
    // production wiring (App.axaml.cs) must pass it explicitly via this
    // constant rather than rely on that default — see SellerSession's XML
    // comment. The design calls for this to eventually come from
    // cash-register config; until that plumbing exists, this constant is the
    // single place a later task needs to touch to read it from settings
    // instead.
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(90);
}
