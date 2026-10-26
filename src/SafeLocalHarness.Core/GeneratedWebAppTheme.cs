namespace SafeLocalHarness.Core;

internal static class GeneratedWebAppTheme
{
    internal const string Styles = """
        :root { color-scheme: light; font-family: "Segoe UI Variable", "Yu Gothic UI", sans-serif; color: #1d1d1f; background: #f5f5f7; }
        * { box-sizing: border-box; }
        body { margin: 0; min-height: 100vh; background: linear-gradient(180deg, #fbfbfd, #f1f2f5); }
        #app { width: min(920px, calc(100% - 32px)); margin: 0 auto; padding: 56px 0 72px; }
        .ls-generated-header { margin-bottom: 28px; }
        .ls-generated-header h1 { margin: 6px 0 10px; font-size: clamp(32px, 6vw, 52px); letter-spacing: -.035em; }
        .ls-generated-header p { color: #6e6e73; font-size: 16px; line-height: 1.7; }
        .ls-eyebrow { color: #0071e3 !important; font-size: 12px !important; font-weight: 700; letter-spacing: .14em; }
        section, .panel { background: rgba(255,255,255,.94); border: 1px solid rgba(0,0,0,.08); border-radius: 22px; padding: 24px; box-shadow: 0 16px 45px rgba(0,0,0,.08); margin: 18px 0; }
        .toolbar, .grid { display: flex; flex-wrap: wrap; gap: 12px; align-items: end; }
        .grid > * { flex: 1 1 180px; }
        label { display: grid; gap: 7px; color: #515154; font-size: 13px; font-weight: 600; }
        input, textarea, select { width: 100%; border: 1px solid #d2d2d7; border-radius: 12px; padding: 11px 13px; font: inherit; background: #fff; color: #1d1d1f; }
        textarea { min-height: 150px; resize: vertical; }
        button { border: 0; border-radius: 999px; padding: 11px 19px; background: #e8e8ed; color: #1d1d1f; font: inherit; font-weight: 600; cursor: pointer; }
        button:hover { filter: brightness(.97); }
        button:focus-visible, input:focus-visible, textarea:focus-visible, select:focus-visible { outline: 3px solid rgba(0,113,227,.3); outline-offset: 2px; }
        button.primary { background: #0071e3; color: white; }
        button.danger { color: #b42318; background: #fff0ee; }
        .stat { background: #f0f6ff; border-radius: 16px; padding: 16px; font-size: 24px; font-weight: 700; }
        ul, ol { padding-left: 22px; }
        .ls-status { min-height: 1.5em; color: #0071e3; margin: 14px 4px; }
        @media (max-width: 600px) { #app { padding-top: 32px; } section, .panel { padding: 18px; border-radius: 18px; } }
        @media (prefers-reduced-motion: reduce) { * { scroll-behavior: auto !important; transition: none !important; } }
        """ + "\n";
}
