var _t = Object.defineProperty;
var Mt = (t, e, a) => e in t ? _t(t, e, { enumerable: !0, configurable: !0, writable: !0, value: a }) : t[e] = a;
var ke = (t, e, a) => Mt(t, typeof e != "symbol" ? e + "" : e, a);
import { useMemo as Ut, useState as k, useId as bt, useRef as H, useEffect as V, useCallback as ye } from "react";
import { jsx as n, jsxs as s, Fragment as Y } from "react/jsx-runtime";
import { Loader2 as Bt, ChevronUp as xt, ChevronDown as vt, X as he, Undo2 as jt, AlertTriangle as Ce } from "lucide-react";
const zt = "/api";
async function A(t, e = {}) {
  const a = `${zt}${t}`, l = await fetch(a, {
    ...e,
    headers: {
      "Content-Type": "application/json",
      ...e.headers
    }
  });
  if (!l.ok) {
    const o = await l.text().catch(() => "");
    throw new Q(l.status, o || l.statusText, t);
  }
  if (l.status !== 204)
    return l.json();
}
class Q extends Error {
  constructor(a, l, o) {
    super(`API ${a} ${o}: ${l}`);
    ke(this, "status");
    ke(this, "body");
    ke(this, "path");
    this.status = a, this.body = l, this.path = o, this.name = "ApiError";
  }
}
function Kt(t) {
  const e = `/extensions/${t}/data`;
  return {
    get: (a) => A(`${e}/${encodeURIComponent(a)}`).then((l) => l.value),
    set: (a, l) => A(e, { method: "POST", body: JSON.stringify({ key: a, value: l }) }),
    delete: (a) => A(`${e}/${encodeURIComponent(a)}`, { method: "DELETE" }),
    getAll: () => A(`${e}`)
  };
}
function qt(t) {
  return Ut(() => Kt(t), [t]);
}
const P = {
  FilenameTemplate: "{$date - }$title{ [$height]}",
  FolderTemplate: "",
  DateFormat: "yyyy-MM-dd",
  // C# verbatim string @"hh\-mm\-ss" → the literal value contains single backslashes.
  DurationFormat: "hh\\-mm\\-ss",
  Performers: {
    Separator: ", ",
    MaxCount: 0,
    OnOverflow: "DropAll",
    Sort: "NameAsc",
    Whitelist: [],
    Blacklist: [],
    IgnoreGenders: [],
    GenderOrder: []
  },
  Tags: {
    Separator: " ",
    MaxCount: 0,
    OnOverflow: "DropAll",
    Sort: "NameAsc",
    Whitelist: [],
    Blacklist: [],
    IgnoreGenders: [],
    GenderOrder: []
  },
  IllegalReplacement: "",
  SpaceReplacement: "",
  RemoveCharacters: "",
  Case: "None",
  AsciiTransliterate: !1,
  FilenameMax: 255,
  FullPathMax: 259,
  DropOrder: [
    "videoCodec",
    "audioCodec",
    "frameRate",
    "resolution",
    "tags",
    "studioCode",
    "studio",
    "performers",
    "date"
  ],
  OnlyOrganized: !1,
  FilenameAsTitle: !0,
  RequiredFields: ["title"],
  DuplicateSuffixFormat: " ({n})",
  AutoRenamerOnUpdate: !1,
  StudioDestinations: {},
  TagDestinations: {},
  PathDestinations: [],
  ExcludeTags: [],
  ExcludeStudioIds: [],
  ExcludePaths: [],
  AllowedRoots: [],
  AssociatedExtensions: [],
  DefaultDestination: "",
  UnorganizedDestination: "",
  EnableDefaultRelocate: !1,
  EnableStudioDestinations: !1,
  EnableTagDestinations: !1,
  EnableAdvancedRouting: !1,
  RemoveEmptyFolder: !1,
  SqueezeStudioNames: !1,
  FieldReplacers: [],
  StripLeadingArticles: !1,
  Articles: ["The", "A", "An"],
  PreventTitlePerformer: !1,
  PreventConsecutiveSegments: !0
};
function xe() {
  return {
    ...P,
    Performers: {
      ...P.Performers,
      Whitelist: [],
      Blacklist: [],
      IgnoreGenders: [],
      GenderOrder: []
    },
    Tags: {
      ...P.Tags,
      Whitelist: [],
      Blacklist: [],
      IgnoreGenders: [],
      GenderOrder: []
    },
    DropOrder: [...P.DropOrder],
    RequiredFields: [...P.RequiredFields],
    StudioDestinations: { ...P.StudioDestinations },
    TagDestinations: { ...P.TagDestinations },
    PathDestinations: P.PathDestinations.map((t) => ({ ...t })),
    ExcludeTags: [...P.ExcludeTags],
    ExcludeStudioIds: [...P.ExcludeStudioIds],
    ExcludePaths: P.ExcludePaths.map((t) => ({ ...t })),
    AllowedRoots: [...P.AllowedRoots],
    AssociatedExtensions: [...P.AssociatedExtensions],
    FieldReplacers: P.FieldReplacers.map((t) => ({ ...t })),
    Articles: [...P.Articles]
  };
}
function Ie(t) {
  return t && typeof t == "object" ? t : {};
}
function O(t, e) {
  return typeof t == "string" ? t : e;
}
function Oe(t, e) {
  return typeof t == "number" && Number.isFinite(t) ? t : e;
}
function M(t, e) {
  return typeof t == "boolean" ? t : e;
}
function X(t, e) {
  return Array.isArray(t) ? t.filter((a) => typeof a == "string") : e;
}
function Gt(t, e) {
  return Array.isArray(t) ? t.filter((a) => typeof a == "number" && Number.isFinite(a)) : e;
}
function Wt(t) {
  const e = Ie(t), a = {};
  for (const [l, o] of Object.entries(e)) {
    const i = Number(l);
    Number.isInteger(i) && typeof o == "string" && (a[i] = o);
  }
  return a;
}
function Ht(t) {
  const e = Ie(t), a = {};
  for (const [l, o] of Object.entries(e))
    typeof o == "string" && (a[l] = o);
  return a;
}
function Vt(t) {
  return Array.isArray(t) ? t.filter((e) => e && typeof e == "object").map((e) => {
    const a = e;
    return {
      Pattern: O(a.Pattern, ""),
      Dest: O(a.Dest, ""),
      IsRegex: M(a.IsRegex, !1)
    };
  }) : [];
}
function Jt(t) {
  return Array.isArray(t) ? t.filter((e) => e && typeof e == "object").map((e) => {
    const a = e;
    return { Pattern: O(a.Pattern, ""), IsRegex: M(a.IsRegex, !1) };
  }) : [];
}
function Yt(t) {
  return Array.isArray(t) ? t.filter((e) => e && typeof e == "object").map((e) => {
    const a = e;
    return {
      TargetToken: O(a.TargetToken, ""),
      Find: O(a.Find, ""),
      Replace: O(a.Replace, "")
    };
  }) : [];
}
function Xt(t) {
  return t === "KeepFirst" ? "KeepFirst" : "DropAll";
}
function Zt(t) {
  return t === "None" || t === "IdAsc" || t === "FavoriteFirst" ? t : "NameAsc";
}
function Qt(t) {
  return t === "Lower" || t === "Title" ? t : "None";
}
function We(t, e) {
  const a = Ie(t);
  return {
    Separator: O(a.Separator, e.Separator),
    MaxCount: Oe(a.MaxCount, e.MaxCount),
    OnOverflow: Xt(a.OnOverflow),
    Sort: Zt(a.Sort),
    Whitelist: X(a.Whitelist, []),
    Blacklist: X(a.Blacklist, []),
    IgnoreGenders: X(a.IgnoreGenders, []),
    GenderOrder: X(a.GenderOrder, [])
  };
}
const en = new Set(Object.keys(P));
function tn(t) {
  if (!t || typeof t != "object") return {};
  const e = {};
  for (const [a, l] of Object.entries(t))
    en.has(a) || (e[a] = l);
  return e;
}
function nn(t) {
  if (!t || typeof t != "object") return xe();
  const e = t, a = P;
  return {
    FilenameTemplate: O(e.FilenameTemplate, a.FilenameTemplate),
    FolderTemplate: O(e.FolderTemplate, a.FolderTemplate),
    DateFormat: O(e.DateFormat, a.DateFormat),
    DurationFormat: O(e.DurationFormat, a.DurationFormat),
    Performers: We(e.Performers, a.Performers),
    Tags: We(e.Tags, a.Tags),
    IllegalReplacement: O(e.IllegalReplacement, a.IllegalReplacement),
    SpaceReplacement: O(e.SpaceReplacement, a.SpaceReplacement),
    RemoveCharacters: O(e.RemoveCharacters, a.RemoveCharacters),
    Case: Qt(e.Case),
    AsciiTransliterate: M(e.AsciiTransliterate, a.AsciiTransliterate),
    FilenameMax: Oe(e.FilenameMax, a.FilenameMax),
    FullPathMax: Oe(e.FullPathMax, a.FullPathMax),
    DropOrder: X(e.DropOrder, [...a.DropOrder]),
    OnlyOrganized: M(e.OnlyOrganized, a.OnlyOrganized),
    FilenameAsTitle: M(e.FilenameAsTitle, a.FilenameAsTitle),
    RequiredFields: X(e.RequiredFields, [...a.RequiredFields]),
    DuplicateSuffixFormat: O(e.DuplicateSuffixFormat, a.DuplicateSuffixFormat),
    AutoRenamerOnUpdate: M(e.AutoRenamerOnUpdate, a.AutoRenamerOnUpdate),
    StudioDestinations: Wt(e.StudioDestinations),
    TagDestinations: Ht(e.TagDestinations),
    PathDestinations: Vt(e.PathDestinations),
    ExcludeTags: X(e.ExcludeTags, []),
    ExcludeStudioIds: Gt(e.ExcludeStudioIds, []),
    ExcludePaths: Jt(e.ExcludePaths),
    AllowedRoots: X(e.AllowedRoots, []),
    AssociatedExtensions: X(e.AssociatedExtensions, [...a.AssociatedExtensions]),
    DefaultDestination: O(e.DefaultDestination, a.DefaultDestination),
    UnorganizedDestination: O(e.UnorganizedDestination, a.UnorganizedDestination),
    EnableDefaultRelocate: M(e.EnableDefaultRelocate, a.EnableDefaultRelocate),
    EnableStudioDestinations: M(e.EnableStudioDestinations, a.EnableStudioDestinations),
    EnableTagDestinations: M(e.EnableTagDestinations, a.EnableTagDestinations),
    EnableAdvancedRouting: M(e.EnableAdvancedRouting, a.EnableAdvancedRouting),
    RemoveEmptyFolder: M(e.RemoveEmptyFolder, a.RemoveEmptyFolder),
    SqueezeStudioNames: M(e.SqueezeStudioNames, a.SqueezeStudioNames),
    FieldReplacers: Yt(e.FieldReplacers),
    StripLeadingArticles: M(e.StripLeadingArticles, a.StripLeadingArticles),
    Articles: X(e.Articles, [...a.Articles]),
    PreventTitlePerformer: M(e.PreventTitlePerformer, a.PreventTitlePerformer),
    PreventConsecutiveSegments: M(e.PreventConsecutiveSegments, a.PreventConsecutiveSegments)
  };
}
function an(t) {
  if (t.length === 0) return { valid: !0 };
  try {
    return new RegExp(t), { valid: !0 };
  } catch (e) {
    return { valid: !1, message: e instanceof Error ? e.message : String(e) };
  }
}
function rn(t) {
  const e = t.trim();
  return e.length === 0 ? !0 : /^[A-Za-z]:[\\/]/.test(e) || /^[\\/]/.test(e);
}
const ln = /* @__PURE__ */ new Set([
  "mp4",
  "mkv",
  "avi",
  "mov",
  "wmv",
  "jpg",
  "jpeg",
  "png",
  "gif",
  "webp",
  "mp3",
  "flac",
  "wav",
  "m4a"
]);
function on(t) {
  return t.length === 0 ? null : /^[a-z0-9]+$/.test(t) ? ln.has(t) ? "This looks like a primary media extension, not a sidecar." : null : "Extensions are letters and numbers only, like srt or nfo.";
}
function sn(t, e) {
  const a = t.trim().toLowerCase();
  return a.length === 0 ? [...e] : e.filter((l) => l.name.toLowerCase().includes(a));
}
function cn(t, e, a) {
  if (e.length === 0) return [...t];
  const l = new Set(e);
  return t.filter((o) => !l.has(a(o)));
}
function dn(t, e) {
  const a = new Set(e);
  return t.filter((l) => !a.has(l.value));
}
function yt(t, e) {
  const a = e.find((l) => l.id === t);
  return a ? a.name : `#${t} (missing)`;
}
function un(t, e) {
  return e.some((a) => a.id === t);
}
function kt(t, e) {
  const a = t.trim(), l = e.find((o) => o.name.toLowerCase() === a.toLowerCase());
  return l ? l.name : a;
}
const se = "w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none", Nt = "cursor-pointer rounded-lg border px-2 py-1 text-xs", mn = "border-border bg-card text-foreground hover:border-accent/50 hover:text-accent", hn = "border-accent bg-accent/15 text-foreground";
function Fe(t) {
  return `${Nt} ${t ? hn : mn}`;
}
function W({
  selected: t,
  onClick: e,
  disabled: a,
  title: l,
  mono: o,
  children: i
}) {
  return /* @__PURE__ */ n(
    "button",
    {
      type: "button",
      onClick: e,
      disabled: a,
      title: l,
      className: o ? `${Fe(t)} font-mono` : Fe(t),
      children: i
    }
  );
}
const Ae = "__custom__";
function w({
  label: t,
  helper: e,
  children: a
}) {
  return /* @__PURE__ */ s("label", { className: "block text-sm", title: e, children: [
    /* @__PURE__ */ n("span", { className: "mb-1 block text-xs font-medium uppercase tracking-wide text-muted", children: t }),
    a,
    e ? /* @__PURE__ */ n("span", { className: "mt-1 block text-xs text-secondary", children: e }) : null
  ] });
}
function U({
  value: t,
  onChange: e,
  onFocus: a,
  placeholder: l,
  mono: o = !1,
  inputRef: i
}) {
  return /* @__PURE__ */ n(
    "input",
    {
      ref: i,
      type: "text",
      value: t,
      placeholder: l,
      onChange: (c) => {
        e(c.target.value);
      },
      onFocus: a,
      className: o ? `${se} font-mono` : se
    }
  );
}
function Ne({
  value: t,
  onChange: e,
  min: a
}) {
  return /* @__PURE__ */ n(
    "input",
    {
      type: "number",
      value: Number.isNaN(t) ? "" : t,
      min: a,
      onChange: (l) => {
        e(l.target.value === "" ? 0 : Number(l.target.value));
      },
      className: `themed-number-input ${se}`
    }
  );
}
function ue({
  value: t,
  onChange: e,
  options: a
}) {
  return /* @__PURE__ */ n(
    "select",
    {
      value: t,
      onChange: (l) => {
        e(l.target.value);
      },
      className: se,
      children: a.map((l) => /* @__PURE__ */ n("option", { value: l.value, children: l.label }, l.value))
    }
  );
}
function De({
  value: t,
  onChange: e,
  options: a,
  customPlaceholder: l
}) {
  const o = a.find((m) => m.value === t), i = o === void 0, c = i ? Ae : t, h = o ? `${o.value} → ${o.example}` : t;
  return /* @__PURE__ */ s("div", { children: [
    /* @__PURE__ */ s(
      "select",
      {
        value: c,
        onChange: (m) => {
          const p = m.target.value;
          p === Ae ? i || e("") : e(p);
        },
        className: se,
        children: [
          a.map((m) => /* @__PURE__ */ s("option", { value: m.value, children: [
            m.value,
            " → ",
            m.example
          ] }, m.value)),
          /* @__PURE__ */ n("option", { value: Ae, children: "Custom…" })
        ]
      }
    ),
    i ? /* @__PURE__ */ n("div", { className: "mt-2", children: /* @__PURE__ */ n(U, { value: t, onChange: e, placeholder: l, mono: !0 }) }) : /* @__PURE__ */ n("span", { className: "mt-1 block font-mono text-xs text-secondary", children: h })
  ] });
}
function He({
  value: t,
  onChange: e,
  options: a,
  customPlaceholder: l
}) {
  const o = !a.some((i) => i.value === t);
  return /* @__PURE__ */ s("div", { children: [
    /* @__PURE__ */ s("div", { className: "flex flex-wrap gap-1", children: [
      a.map((i) => {
        const c = i.value === t;
        return /* @__PURE__ */ n(
          W,
          {
            selected: c,
            onClick: () => {
              e(i.value);
            },
            children: i.label
          },
          i.value || "__empty__"
        );
      }),
      /* @__PURE__ */ n(
        W,
        {
          selected: o,
          onClick: () => {
            o || e("");
          },
          children: "Custom"
        }
      )
    ] }),
    o ? /* @__PURE__ */ n("div", { className: "mt-2", children: /* @__PURE__ */ n(U, { value: t, onChange: e, placeholder: l, mono: !0 }) }) : null
  ] });
}
function Ve({
  value: t,
  onChange: e,
  stripLabel: a,
  replaceLabel: l,
  stripHelper: o,
  replaceHelper: i,
  inputPlaceholder: c
}) {
  const h = H(null), [m, p] = k(t !== ""), g = H(t);
  V(() => {
    t !== "" ? p(!0) : g.current !== "" && p(!1), g.current = t;
  }, [t]);
  const u = m || t !== "";
  function f() {
    p(!1), t !== "" && e("");
  }
  function b() {
    p(!0), requestAnimationFrame(() => {
      var x;
      return (x = h.current) == null ? void 0 : x.focus();
    });
  }
  return /* @__PURE__ */ s("div", { children: [
    /* @__PURE__ */ s("div", { className: "flex gap-1", children: [
      /* @__PURE__ */ n(W, { selected: !u, onClick: f, children: a }),
      /* @__PURE__ */ n(W, { selected: u, onClick: b, children: l })
    ] }),
    u ? /* @__PURE__ */ s("div", { className: "mt-2", children: [
      /* @__PURE__ */ n(
        U,
        {
          value: t,
          onChange: e,
          placeholder: c,
          inputRef: h,
          mono: !0
        }
      ),
      i ? /* @__PURE__ */ n("span", { className: "mt-1 block text-xs text-secondary", children: i }) : null
    ] }) : o ? /* @__PURE__ */ n("span", { className: "mt-1 block text-xs text-secondary", children: o }) : null
  ] });
}
function _({
  label: t,
  checked: e,
  onChange: a,
  helper: l
}) {
  return /* @__PURE__ */ s("div", { children: [
    /* @__PURE__ */ s("label", { className: "flex items-center gap-2 text-sm text-secondary", title: l, children: [
      /* @__PURE__ */ n(
        "button",
        {
          type: "button",
          role: "switch",
          "aria-checked": e,
          onClick: () => {
            a(!e);
          },
          className: `inline-flex h-5 w-9 items-center rounded-full transition-colors ${e ? "bg-accent" : "bg-card border border-border"}`,
          children: /* @__PURE__ */ n(
            "span",
            {
              className: `inline-block h-4 w-4 rounded-full bg-white transition-transform ${e ? "translate-x-4" : "translate-x-0.5"}`
            }
          )
        }
      ),
      /* @__PURE__ */ n("span", { children: t })
    ] }),
    l ? /* @__PURE__ */ n("p", { className: "mt-1 text-xs text-secondary", children: l }) : null
  ] });
}
function be({
  values: t,
  onChange: e,
  placeholder: a,
  ordered: l = !1,
  normalize: o,
  onReject: i,
  onLiveChange: c
}) {
  const h = bt();
  function m(u) {
    const f = (o ? o(u.value) : u.value).trim();
    f.length !== 0 && (i != null && i(f) || (t.includes(f) || e([...t, f]), u.value = ""));
  }
  function p(u) {
    e(t.filter((f, b) => b !== u));
  }
  function g(u, f) {
    const b = u + f;
    if (b < 0 || b >= t.length) return;
    const x = [...t];
    [x[u], x[b]] = [x[b], x[u]], e(x);
  }
  return /* @__PURE__ */ s("div", { children: [
    t.length > 0 ? /* @__PURE__ */ n("div", { className: "mb-1 flex flex-wrap gap-1", children: t.map((u, f) => /* @__PURE__ */ s(
      "span",
      {
        className: "inline-flex items-center gap-1 rounded-lg border border-border bg-card px-2 py-0.5 text-xs text-foreground",
        children: [
          l ? /* @__PURE__ */ s(Y, { children: [
            /* @__PURE__ */ n(
              "button",
              {
                type: "button",
                "aria-label": `Move ${u} up`,
                onClick: () => {
                  g(f, -1);
                },
                className: "text-muted hover:text-foreground",
                children: "↑"
              }
            ),
            /* @__PURE__ */ n(
              "button",
              {
                type: "button",
                "aria-label": `Move ${u} down`,
                onClick: () => {
                  g(f, 1);
                },
                className: "text-muted hover:text-foreground",
                children: "↓"
              }
            )
          ] }) : null,
          /* @__PURE__ */ n("span", { className: "font-mono", children: u }),
          /* @__PURE__ */ n(
            "button",
            {
              type: "button",
              "aria-label": `Remove ${u}`,
              onClick: () => {
                p(f);
              },
              className: "text-muted hover:text-foreground",
              children: /* @__PURE__ */ n(he, { className: "h-3 w-3" })
            }
          )
        ]
      },
      `${u}-${f}`
    )) }) : null,
    /* @__PURE__ */ n(
      "input",
      {
        id: h,
        type: "text",
        placeholder: a,
        className: se,
        onChange: (u) => {
          c == null || c(u.target.value);
        },
        onKeyDown: (u) => {
          u.key === "Enter" && (u.preventDefault(), m(u.currentTarget));
        },
        onBlur: (u) => {
          m(u.currentTarget);
        }
      }
    )
  ] });
}
function pn({
  options: t,
  values: e,
  onChange: a
}) {
  const l = new Set(t.map((h) => h.value)), o = e.filter((h) => !l.has(h));
  function i(h) {
    const m = e.includes(h), p = t.map((g) => g.value).filter((g) => g === h ? !m : e.includes(g));
    a([...p, ...o]);
  }
  function c(h) {
    a(e.filter((m) => m !== h));
  }
  return /* @__PURE__ */ s("div", { className: "flex flex-wrap gap-1", children: [
    t.map((h) => {
      const m = e.includes(h.value);
      return /* @__PURE__ */ n(
        W,
        {
          selected: m,
          onClick: () => {
            i(h.value);
          },
          children: h.label
        },
        h.value
      );
    }),
    o.map((h) => /* @__PURE__ */ s(
      "button",
      {
        type: "button",
        onClick: () => {
          c(h);
        },
        className: `${Fe(!0)} inline-flex items-center gap-1`,
        title: "Not a recognized value — click to remove",
        children: [
          h,
          /* @__PURE__ */ n(he, { className: "h-3 w-3" })
        ]
      },
      `extra:${h}`
    ))
  ] });
}
function fn({
  options: t,
  values: e,
  onChange: a,
  addPrompt: l
}) {
  const o = (m) => {
    var p;
    return ((p = t.find((g) => g.value === m)) == null ? void 0 : p.label) ?? m;
  }, i = dn(t, e);
  function c(m, p) {
    const g = m + p;
    if (g < 0 || g >= e.length) return;
    const u = [...e];
    [u[m], u[g]] = [u[g], u[m]], a(u);
  }
  function h(m) {
    a(e.filter((p, g) => g !== m));
  }
  return /* @__PURE__ */ s("div", { children: [
    e.length > 0 ? /* @__PURE__ */ n("div", { className: "mb-1 flex flex-wrap gap-1", children: e.map((m, p) => /* @__PURE__ */ s(
      "span",
      {
        className: "inline-flex items-center gap-1 rounded-lg border border-border bg-card px-2 py-0.5 text-xs text-foreground",
        children: [
          /* @__PURE__ */ n(
            "button",
            {
              type: "button",
              "aria-label": `Move ${o(m)} up`,
              onClick: () => {
                c(p, -1);
              },
              className: "text-muted hover:text-foreground",
              children: "↑"
            }
          ),
          /* @__PURE__ */ n(
            "button",
            {
              type: "button",
              "aria-label": `Move ${o(m)} down`,
              onClick: () => {
                c(p, 1);
              },
              className: "text-muted hover:text-foreground",
              children: "↓"
            }
          ),
          /* @__PURE__ */ n("span", { children: o(m) }),
          /* @__PURE__ */ n(
            "button",
            {
              type: "button",
              "aria-label": `Remove ${o(m)}`,
              onClick: () => {
                h(p);
              },
              className: "text-muted hover:text-foreground",
              children: /* @__PURE__ */ n(he, { className: "h-3 w-3" })
            }
          )
        ]
      },
      m
    )) }) : null,
    i.length > 0 ? /* @__PURE__ */ s(
      "select",
      {
        value: "",
        onChange: (m) => {
          const p = m.target.value;
          p !== "" && a([...e, p]);
        },
        className: se,
        children: [
          /* @__PURE__ */ n("option", { value: "", children: l }),
          i.map((m) => /* @__PURE__ */ n("option", { value: m.value, children: m.label }, m.value))
        ]
      }
    ) : null
  ] });
}
function Je({
  tokens: t,
  values: e,
  onAdd: a
}) {
  return /* @__PURE__ */ s("div", { className: "mt-1", children: [
    /* @__PURE__ */ n("span", { className: "mb-1 block text-xs text-muted", children: "Add a token:" }),
    /* @__PURE__ */ n("div", { className: "flex flex-wrap gap-1", children: t.map((l) => e.includes(l) ? (
      // Already-added tokens render a distinct muted/disabled treatment (text-muted, no hover),
      // not the standard unselected chip — so this branch stays direct markup rather than <Chip>.
      /* @__PURE__ */ n(
        "button",
        {
          type: "button",
          disabled: !0,
          className: `${Nt} border-border bg-card text-muted font-mono`,
          children: l
        },
        l
      )
    ) : /* @__PURE__ */ n(
      W,
      {
        selected: !1,
        mono: !0,
        onClick: () => {
          a(l);
        },
        children: l
      },
      l
    )) })
  ] });
}
function Pe({
  rows: t,
  onChange: e,
  makeRow: a,
  renderRow: l,
  addLabel: o,
  ordered: i = !1
}) {
  const [c, h] = k(() => t.map((b, x) => x)), m = H(t.length);
  V(() => {
    c.length !== t.length && (m.current = t.length, h(t.map((b, x) => x)));
  }, [t, c.length]);
  function p(b, x) {
    e(t.map((d, N) => N === b ? { ...d, ...x } : d));
  }
  function g(b) {
    e(t.filter((x, d) => d !== b)), h((x) => x.filter((d, N) => N !== b));
  }
  function u(b, x) {
    const d = b + x;
    if (d < 0 || d >= t.length) return;
    const N = [...t];
    [N[b], N[d]] = [N[d], N[b]], e(N), h((z) => {
      const R = [...z];
      return [R[b], R[d]] = [R[d], R[b]], R;
    });
  }
  function f() {
    e([...t, a()]), h((b) => [...b, m.current++]);
  }
  return /* @__PURE__ */ s("div", { className: "space-y-2", children: [
    t.map((b, x) => /* @__PURE__ */ s(
      "div",
      {
        className: "flex items-start gap-2 rounded-xl border border-border bg-card p-3",
        children: [
          /* @__PURE__ */ n("div", { className: "min-w-0 flex-1 space-y-2", children: l(b, x, (d) => {
            p(x, d);
          }) }),
          i ? /* @__PURE__ */ s("span", { className: "flex flex-col text-muted", children: [
            /* @__PURE__ */ n(
              "button",
              {
                type: "button",
                "aria-label": `Move row ${x + 1} up`,
                onClick: () => {
                  u(x, -1);
                },
                className: "hover:text-foreground",
                children: /* @__PURE__ */ n(xt, { className: "h-4 w-4" })
              }
            ),
            /* @__PURE__ */ n(
              "button",
              {
                type: "button",
                "aria-label": `Move row ${x + 1} down`,
                onClick: () => {
                  u(x, 1);
                },
                className: "hover:text-foreground",
                children: /* @__PURE__ */ n(vt, { className: "h-4 w-4" })
              }
            )
          ] }) : null,
          /* @__PURE__ */ n(
            "button",
            {
              type: "button",
              "aria-label": `Remove row ${x + 1}`,
              onClick: () => {
                g(x);
              },
              className: "text-muted hover:text-foreground",
              children: /* @__PURE__ */ n(he, { className: "h-4 w-4" })
            }
          )
        ]
      },
      c.length === t.length ? c[x] : x
    )),
    /* @__PURE__ */ n(G, { variant: "ghost", onClick: f, children: o })
  ] });
}
function St({
  map: t,
  onChange: e,
  renderKey: a,
  renderValue: l,
  renderKeyLabel: o,
  addLabel: i
}) {
  const [c, h] = k(""), [m, p] = k(""), g = Object.keys(t);
  function u(d, N) {
    e({ ...t, [d]: N });
  }
  function f(d) {
    e(Object.fromEntries(Object.entries(t).filter(([N]) => N !== d)));
  }
  function b() {
    const d = c.trim();
    d.length === 0 || d in t || (e({ ...t, [d]: m }), h(""), p(""));
  }
  const x = c.trim().length > 0 && c.trim() in t;
  return /* @__PURE__ */ s("div", { className: "space-y-2", children: [
    g.map((d) => /* @__PURE__ */ s(
      "div",
      {
        className: "flex items-center gap-2 rounded-xl border border-border bg-card p-3",
        children: [
          /* @__PURE__ */ n("span", { className: "min-w-0 flex-1 truncate font-mono text-sm text-foreground", children: o ? o(d) : d }),
          /* @__PURE__ */ n("span", { className: "flex-1", children: l(t[d], (N) => {
            u(d, N);
          }) }),
          /* @__PURE__ */ n(
            "button",
            {
              type: "button",
              "aria-label": `Remove ${d}`,
              onClick: () => {
                f(d);
              },
              className: "text-muted hover:text-foreground",
              children: /* @__PURE__ */ n(he, { className: "h-4 w-4" })
            }
          )
        ]
      },
      d
    )),
    /* @__PURE__ */ s("div", { className: "flex items-start gap-2 rounded-xl border border-border bg-card p-3", children: [
      /* @__PURE__ */ n("span", { className: "min-w-0 flex-1", children: a(c, h, g) }),
      /* @__PURE__ */ n("span", { className: "flex-1", children: l(m, p) }),
      /* @__PURE__ */ n(G, { variant: "ghost", onClick: b, disabled: c.trim().length === 0 || x, children: i })
    ] }),
    x ? /* @__PURE__ */ n(I, { kind: "error", children: "That key already has a value." }) : null
  ] });
}
function Ye({ pattern: t, isRegex: e }) {
  if (!e) return null;
  const a = an(t);
  return a.valid ? null : /* @__PURE__ */ s(I, { kind: "error", children: [
    "Invalid pattern: ",
    a.message
  ] });
}
function ve({ value: t }) {
  return t.trim().length === 0 || rn(t) ? null : /* @__PURE__ */ n(I, { kind: "warning", children: "Doesn't look like an absolute path." });
}
function q({
  title: t,
  description: e,
  headerRight: a,
  children: l
}) {
  return /* @__PURE__ */ s("div", { className: "rounded-xl border border-border bg-card p-4", children: [
    a ? /* @__PURE__ */ s("div", { className: "flex items-center justify-between gap-4", children: [
      /* @__PURE__ */ n("h3", { className: "text-base font-semibold text-foreground", children: t }),
      a
    ] }) : /* @__PURE__ */ n("h3", { className: "text-base font-semibold text-foreground", children: t }),
    e ? /* @__PURE__ */ n("p", { className: "mb-4 mt-1 text-sm text-secondary", children: e }) : /* @__PURE__ */ n("div", { className: "mb-4" }),
    /* @__PURE__ */ n("div", { className: "space-y-4", children: l })
  ] });
}
function F({
  title: t,
  summary: e,
  defaultOpen: a = !1,
  children: l
}) {
  const [o, i] = k(a);
  return /* @__PURE__ */ s("div", { className: "overflow-hidden rounded-xl border border-border", children: [
    /* @__PURE__ */ s(
      "button",
      {
        type: "button",
        onClick: () => {
          i((c) => !c);
        },
        "aria-expanded": o,
        className: "flex w-full items-center justify-between gap-4 bg-card px-4 py-3 text-left transition-colors hover:bg-card-hover",
        children: [
          /* @__PURE__ */ s("span", { className: "min-w-0", children: [
            /* @__PURE__ */ n("span", { className: "block text-sm font-medium text-foreground", children: t }),
            e ? /* @__PURE__ */ n("span", { className: "mt-1 block truncate text-xs text-muted", children: e }) : null
          ] }),
          o ? /* @__PURE__ */ n(xt, { className: "h-4 w-4 shrink-0 text-muted" }) : /* @__PURE__ */ n(vt, { className: "h-4 w-4 shrink-0 text-muted" })
        ]
      }
    ),
    o ? /* @__PURE__ */ n("div", { className: "space-y-4 border-t border-border px-4 py-4", children: l }) : null
  ] });
}
function G({
  variant: t = "primary",
  children: e,
  onClick: a,
  disabled: l
}) {
  return /* @__PURE__ */ n("button", { type: "button", onClick: a, disabled: l, className: t === "ghost" ? "inline-flex items-center gap-1.5 rounded-lg border border-border bg-card px-3 py-2 text-sm font-medium text-secondary hover:border-accent/50 hover:bg-card-hover hover:text-foreground disabled:opacity-60" : "inline-flex items-center gap-2 rounded-lg bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-60", children: e });
}
function I({ kind: t, children: e }) {
  return /* @__PURE__ */ n("span", { className: `text-xs ${t === "success" ? "text-green-400" : t === "error" ? "text-red-400" : t === "warning" ? "text-amber-400" : "text-secondary"}`, children: e });
}
function ee() {
  return /* @__PURE__ */ n(Bt, { className: "h-4 w-4 animate-spin" });
}
const Le = "com.alextomas955.renamer", gn = `/extensions/${Le}/list-studios`, bn = `/extensions/${Le}/list-tags`, xn = `/extensions/${Le}/list-performers`, vn = "w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none", yn = "cursor-pointer rounded-lg px-2 py-1 text-left text-sm text-foreground hover:bg-card-hover", Xe = "inline-flex items-center gap-1 rounded-lg border border-border bg-card px-2 py-0.5 text-xs text-foreground", kn = "border-red-400 text-red-400";
function _e({
  label: t,
  helper: e,
  values: a,
  onChange: l,
  endpointPath: o,
  adapter: i,
  placeholder: c,
  excludeValues: h
}) {
  const m = bt(), [p, g] = k(""), [u, f] = k(!1), [b, x] = k([]), [d, N] = k(!1), [z, R] = k(!1), [Z, te] = k(!1), S = H(!1), K = H(null);
  V(() => {
    if (!u) return;
    const C = (J) => {
      var ce;
      (ce = K.current) != null && ce.contains(J.target) || f(!1);
    };
    return document.addEventListener("mousedown", C), () => {
      document.removeEventListener("mousedown", C);
    };
  }, [u]);
  const ne = ye(async () => {
    if (!(S.current || d)) {
      S.current = !0, R(!0);
      try {
        const C = await A(o);
        x(C), N(!0), te(!1);
      } catch {
        te(!0);
      } finally {
        S.current = !1, R(!1);
      }
    }
  }, [o, d]);
  function ie() {
    f(!0), ne();
  }
  function ae(C) {
    const J = i.toValue(C, b);
    a.includes(J) || l([...a, J]), g(""), f(!1);
  }
  function re(C) {
    l(a.filter((J) => J !== C));
  }
  const le = h ? [...a, ...h] : a, D = cn(b, le, i.valueOf), L = sn(p, D);
  return /* @__PURE__ */ s(w, { label: t, helper: e, children: [
    a.length > 0 ? /* @__PURE__ */ n("div", { className: "mb-1 flex flex-wrap gap-1", children: a.map((C) => {
      const J = d && !i.isResolved(C, b);
      return /* @__PURE__ */ s(
        "span",
        {
          className: J ? `${Xe} ${kn}` : Xe,
          children: [
            /* @__PURE__ */ n("span", { children: i.toLabel(C, b) }),
            /* @__PURE__ */ n(
              "button",
              {
                type: "button",
                "aria-label": `Remove ${i.toLabel(C, b)}`,
                onClick: () => {
                  re(C);
                },
                className: "text-muted hover:text-foreground",
                children: /* @__PURE__ */ n(he, { className: "h-3 w-3" })
              }
            )
          ]
        },
        String(C)
      );
    }) }) : null,
    /* @__PURE__ */ s("div", { className: "relative", ref: K, children: [
      /* @__PURE__ */ n(
        "input",
        {
          id: m,
          type: "text",
          value: p,
          placeholder: c,
          className: vn,
          onFocus: ie,
          onChange: (C) => {
            g(C.target.value), f(!0);
          },
          onKeyDown: (C) => {
            C.key === "Enter" ? (C.preventDefault(), p.trim() !== "" && L.length > 0 && ae(L[0])) : C.key === "Escape" && f(!1);
          }
        }
      ),
      u && !Z ? /* @__PURE__ */ n("div", { className: "mt-1 flex max-h-48 flex-col gap-0.5 overflow-auto rounded-xl border border-border bg-card p-1", children: z ? /* @__PURE__ */ s("span", { className: "flex items-center gap-2 px-2 py-1 text-sm text-muted", children: [
        /* @__PURE__ */ n(ee, {}),
        "Loading…"
      ] }) : L.length === 0 ? /* @__PURE__ */ n("span", { className: "px-2 py-1 text-sm text-muted", children: "No matches" }) : L.map((C) => /* @__PURE__ */ n(
        "button",
        {
          type: "button",
          className: yn,
          onClick: () => {
            ae(C);
          },
          children: C.name
        },
        C.id
      )) }) : null
    ] }),
    Z ? /* @__PURE__ */ n("span", { className: "mt-1 block", children: /* @__PURE__ */ n(I, { kind: "error", children: "Could not load the list — existing values stay editable." }) }) : null
  ] });
}
const Nn = {
  toValue: (t) => t.id,
  valueOf: (t) => t.id,
  toLabel: (t, e) => yt(t, e),
  isResolved: (t, e) => un(t, e)
}, Sn = {
  // A picked row already carries the canonical spelling; canonicalTagName also folds a typed casing.
  toValue: (t, e) => kt(t.name, e),
  valueOf: (t) => t.name,
  toLabel: (t) => t,
  // A tag value is the name itself, so it is always displayable; "resolved" tracks list membership.
  isResolved: (t, e) => e.some((a) => a.name.toLowerCase() === t.toLowerCase())
}, wn = {
  toValue: (t, e) => kt(t.name, e),
  valueOf: (t) => t.name,
  toLabel: (t) => t,
  isResolved: (t, e) => e.some((a) => a.name.toLowerCase() === t.toLowerCase())
};
function wt({
  label: t,
  helper: e,
  values: a,
  onChange: l,
  placeholder: o,
  excludeValues: i
}) {
  return /* @__PURE__ */ n(
    _e,
    {
      label: t,
      helper: e,
      values: a,
      onChange: l,
      endpointPath: gn,
      adapter: Nn,
      placeholder: o,
      excludeValues: i
    }
  );
}
function Se({
  label: t,
  helper: e,
  values: a,
  onChange: l,
  placeholder: o,
  excludeValues: i
}) {
  return /* @__PURE__ */ n(
    _e,
    {
      label: t,
      helper: e,
      values: a,
      onChange: l,
      endpointPath: bn,
      adapter: Sn,
      placeholder: o,
      excludeValues: i
    }
  );
}
function Ze({
  label: t,
  helper: e,
  values: a,
  onChange: l,
  placeholder: o,
  excludeValues: i
}) {
  return /* @__PURE__ */ n(
    _e,
    {
      label: t,
      helper: e,
      values: a,
      onChange: l,
      endpointPath: xn,
      adapter: wn,
      placeholder: o,
      excludeValues: i
    }
  );
}
function Tn(t) {
  const e = {};
  for (const [a, l] of Object.entries(t)) e[a] = l;
  return e;
}
function Cn(t) {
  const e = {};
  for (const [a, l] of Object.entries(t)) {
    const o = Number(a);
    Number.isInteger(o) && typeof l == "string" && (e[o] = l);
  }
  return e;
}
const En = "com.alextomas955.renamer", Rn = `/extensions/${En}/list-studios`;
function $n({
  map: t,
  onChange: e
}) {
  const [a, l] = k([]);
  return V(() => {
    let o = !0;
    return A(Rn).then((i) => {
      o && l(i);
    }).catch(() => {
    }), () => {
      o = !1;
    };
  }, []), /* @__PURE__ */ n(
    St,
    {
      map: Tn(t),
      onChange: (o) => {
        e(Cn(o));
      },
      renderKey: (o, i, c) => /* @__PURE__ */ n(An, { draftKey: o, setDraftKey: i, existingKeys: c }),
      renderValue: (o, i) => /* @__PURE__ */ s(Y, { children: [
        /* @__PURE__ */ n(U, { value: o, onChange: i, placeholder: "Destination root" }),
        /* @__PURE__ */ n(ve, { value: o })
      ] }),
      renderKeyLabel: (o) => yt(Number(o), a),
      addLabel: "Add studio rule"
    }
  );
}
function An({
  draftKey: t,
  setDraftKey: e,
  existingKeys: a
}) {
  const l = t === "" ? [] : [Number(t)], o = a.map(Number);
  return /* @__PURE__ */ n(
    wt,
    {
      label: "Studio",
      values: l,
      onChange: (i) => {
        const c = i.at(-1);
        e(c === void 0 ? "" : String(c));
      },
      placeholder: "Search studios…",
      excludeValues: o
    }
  );
}
const Me = [
  { token: "$title", label: "Title", kind: "core", insert: "$title" },
  { token: "$studio", label: "Studio", kind: "optional", insert: "{ - $studio}" },
  { token: "$studioCode", label: "Studio code", kind: "optional", insert: "{ - $studioCode}" },
  { token: "$date", label: "Date", kind: "optional", insert: "{ - $date}" },
  { token: "$year", label: "Year", kind: "optional", insert: "{ [$year]}" },
  { token: "$height", label: "Height", kind: "optional", insert: "{ [$height]}" },
  { token: "$width", label: "Width", kind: "optional", insert: "{ [$width]}" },
  {
    token: "$resolution",
    label: "Resolution (e.g. 1080p)",
    kind: "optional",
    insert: "{ [$resolution]}"
  },
  { token: "$videoCodec", label: "Video codec", kind: "optional", insert: "{ [$videoCodec]}" },
  { token: "$audioCodec", label: "Audio codec", kind: "optional", insert: "{ [$audioCodec]}" },
  { token: "$frameRate", label: "Frame rate", kind: "optional", insert: "{ [$frameRate]}" },
  { token: "$duration", label: "Duration", kind: "optional", insert: "{ [$duration]}" },
  { token: "$performers", label: "Performers", kind: "optional", insert: "{ - $performers}" },
  { token: "$tags", label: "Tags", kind: "optional", insert: "{ - $tags}" },
  { token: "$ext", label: "Extension", kind: "core", insert: "$ext" }
];
function Dn(t) {
  return `Inserts wrapped in an optional group: ${t.insert} — disappears cleanly when empty.`;
}
function Pn({ onInsert: t }) {
  return /* @__PURE__ */ s("div", { children: [
    /* @__PURE__ */ s("p", { className: "mb-1 text-xs text-muted", children: [
      "Click a token to insert it. ",
      /* @__PURE__ */ n("span", { className: "text-foreground", children: "Optional tokens" }),
      " (marked",
      " ",
      /* @__PURE__ */ n("span", { className: "font-mono", children: "{ }" }),
      ") insert wrapped so they vanish — with their punctuation — when empty. ",
      /* @__PURE__ */ n("span", { className: "text-foreground", children: "Core tokens" }),
      " insert as-is."
    ] }),
    /* @__PURE__ */ n("div", { className: "flex flex-wrap gap-1", children: Me.map((e) => /* @__PURE__ */ s(
      W,
      {
        selected: !1,
        mono: !0,
        title: e.kind === "optional" ? Dn(e) : e.label,
        onClick: () => {
          t(e.insert);
        },
        children: [
          e.token,
          e.kind === "optional" ? /* @__PURE__ */ n("span", { className: "ml-1 text-muted", children: "{ }" }) : null
        ]
      },
      e.token
    )) })
  ] });
}
function On(t, e) {
  switch (t) {
    case "empty":
      return "⚠ This template produces an empty name for this sample.";
    case "sanitized":
      return "⚠ Adjusted: illegal characters were stripped or replaced.";
    case "length-reduced":
      return e.droppedFields.length > 0 ? `⚠ Shortened to fit the path limit — dropped: ${e.droppedFields.join(", ")}.` : "⚠ Shortened to fit the path limit.";
    case "gating-skip":
      return "⚠ Would be skipped: a required field is missing for this sample.";
    default:
      return null;
  }
}
function Fn({ result: t }) {
  return /* @__PURE__ */ s("div", { className: "rounded-xl border border-border bg-card p-4", children: [
    /* @__PURE__ */ s("div", { className: "mb-2 text-xs font-medium uppercase tracking-wide text-muted", children: [
      "Sample: ",
      t.sampleLabel
    ] }),
    t.folder.length > 0 ? /* @__PURE__ */ s("div", { className: "mb-1 text-xs text-secondary", children: [
      t.folder.split("/").join(" / "),
      " /"
    ] }) : null,
    /* @__PURE__ */ n("div", { className: "font-mono text-sm text-muted line-through", children: t.oldName }),
    /* @__PURE__ */ s("div", { className: "font-mono text-sm text-foreground", children: [
      /* @__PURE__ */ n("span", { className: "text-muted", children: "Renamed → " }),
      t.newName
    ] }),
    t.flags.length > 0 ? /* @__PURE__ */ n("div", { className: "mt-2 space-y-1", children: t.flags.map((e) => {
      const a = On(e, t);
      return a ? /* @__PURE__ */ n("p", { className: "text-xs text-amber-400", children: a }, e) : null;
    }) }) : null
  ] });
}
const Qe = 'a[href], button:not([disabled]), textarea, input, select, [tabindex]:not([tabindex="-1"])';
function Tt({
  titleId: t,
  describedById: e,
  pending: a = !1,
  onCancel: l,
  size: o = "lg",
  children: i
}) {
  const c = H(null), h = ye(() => {
    a || l();
  }, [a, l]);
  return V(() => {
    const p = document.activeElement, g = c.current, u = g == null ? void 0 : g.querySelector(Qe);
    return u == null || u.focus(), () => p == null ? void 0 : p.focus();
  }, []), V(() => {
    function p(g) {
      if (g.key === "Escape") {
        g.preventDefault(), h();
        return;
      }
      if (g.key !== "Tab") return;
      const u = c.current;
      if (!u) return;
      const f = Array.from(u.querySelectorAll(Qe));
      if (f.length === 0) return;
      const b = f[0], x = f[f.length - 1], d = document.activeElement;
      g.shiftKey && d === b ? (g.preventDefault(), x.focus()) : !g.shiftKey && d === x && (g.preventDefault(), b.focus());
    }
    return document.addEventListener("keydown", p), () => {
      document.removeEventListener("keydown", p);
    };
  }, [h]), /* @__PURE__ */ s("div", { className: "fixed inset-0 z-50 flex items-center justify-center", children: [
    /* @__PURE__ */ n("div", { className: "fixed inset-0 bg-black/60", onClick: h, "aria-hidden": "true" }),
    /* @__PURE__ */ n(
      "div",
      {
        ref: c,
        role: "dialog",
        "aria-modal": "true",
        "aria-labelledby": t,
        "aria-describedby": e,
        className: `relative ${o === "sm" ? "max-w-sm" : o === "xl" ? "max-w-5xl" : "max-w-2xl"} w-full mx-4 rounded-lg border border-border bg-surface p-6 shadow-xl`,
        children: i
      }
    )
  ] });
}
function In({ children: t }) {
  return /* @__PURE__ */ n("div", { className: "rounded border border-red-700 bg-red-950/60 px-3 py-2 text-sm text-red-200", children: t });
}
const Ct = "com.alextomas955.renamer", Ln = `/extensions/${Ct}/last-batch`, _n = `/extensions/${Ct}/undo`, et = "rename-undo-confirm-title", tt = "rename-undo-confirm-message", Mn = 621355968e5, Et = 1e4, Un = Mn * Et;
function Bn(t) {
  return (t - Un) / Et;
}
function jn(t, e = Date.now()) {
  const a = e - t, l = Math.round(a / 1e3);
  if (l < 45) return "just now";
  const o = Math.round(l / 60);
  if (o < 60) return `${o} minute${o === 1 ? "" : "s"} ago`;
  const i = Math.round(o / 60);
  if (i < 24) return `${i} hour${i === 1 ? "" : "s"} ago`;
  const c = Math.round(i / 24);
  return c === 1 ? "yesterday" : c <= 7 ? `${c} days ago` : new Date(t).toLocaleDateString();
}
function nt(t) {
  return t instanceof Q ? `${t.status} ${t.body}` : String(t);
}
function zn({ refreshKey: t }) {
  const [e, a] = k(null), [l, o] = k(!0), [i, c] = k(null), [h, m] = k(!1), [p, g] = k(!1), [u, f] = k(null), b = ye(async () => {
    o(!0), c(null);
    try {
      const R = await A(Ln);
      a(R);
    } catch (R) {
      c(nt(R));
    } finally {
      o(!1);
    }
  }, []);
  V(() => {
    b();
  }, [b, t]);
  const x = !!e && e.hasBatch && !e.consumed, d = (e == null ? void 0 : e.count) ?? 0, N = e ? Bn(e.writtenAtUtcTicks) : 0;
  async function z() {
    var R, Z, te, S, K, ne, ie, ae, re, le;
    g(!0), f(null);
    try {
      const D = await A(_n, { method: "POST" }), L = (((R = D.failed) == null ? void 0 : R.length) ?? 0) + (((Z = D.skipped) == null ? void 0 : Z.length) ?? 0);
      if (L === 0)
        f({
          kind: "success",
          text: `Undone — ${D.undone} file${D.undone === 1 ? "" : "s"} moved back to their original names.`
        });
      else if (D.undone > 0) {
        const C = ((S = (te = D.failed) == null ? void 0 : te[0]) == null ? void 0 : S.reason) ?? ((ne = (K = D.skipped) == null ? void 0 : K[0]) == null ? void 0 : ne.reason) ?? "unknown reason";
        f({
          kind: "error",
          text: `Undo finished with problems — ${L} file${L === 1 ? "" : "s"} couldn't be moved back (${C}). The rest were restored.`
        });
      } else {
        const C = ((ae = (ie = D.failed) == null ? void 0 : ie[0]) == null ? void 0 : ae.reason) ?? ((le = (re = D.skipped) == null ? void 0 : re[0]) == null ? void 0 : le.reason) ?? "unknown reason";
        f({ kind: "error", text: `Couldn't undo — ${C}. Nothing was changed.` });
      }
    } catch (D) {
      if (D instanceof Q) {
        f({
          kind: "error",
          text: `Couldn't undo — ${nt(D)}. Nothing was changed.`
        });
        return;
      }
      f({
        kind: "success",
        text: "Undone — your files were moved back to their original names."
      });
    } finally {
      g(!1), m(!1), b();
    }
  }
  return /* @__PURE__ */ s("div", { className: "rounded-xl border border-border bg-card p-4", children: [
    /* @__PURE__ */ n("h3", { className: "text-base font-semibold text-foreground", children: "Undo last rename" }),
    /* @__PURE__ */ n("p", { className: "mb-4 mt-1 text-sm text-secondary", children: "This moves every file in that batch back to its original name. It can't be undone again. Undo history is kept in this extension's stored data, so it's lost if that data is cleared." }),
    l ? /* @__PURE__ */ s("div", { className: "flex items-center gap-2 text-sm text-secondary", children: [
      /* @__PURE__ */ n(ee, {}),
      "Checking for a recent rename…"
    ] }) : i ? /* @__PURE__ */ s("div", { className: "space-y-2", children: [
      /* @__PURE__ */ s(I, { kind: "error", children: [
        "Couldn't check for a recent rename — ",
        i,
        "."
      ] }),
      /* @__PURE__ */ n("div", { children: /* @__PURE__ */ n(G, { variant: "ghost", onClick: () => void b(), children: "Retry" }) })
    ] }) : x ? /* @__PURE__ */ s("div", { className: "space-y-3", children: [
      /* @__PURE__ */ s("div", { className: "flex items-center justify-between gap-3", children: [
        /* @__PURE__ */ s("span", { className: "text-sm text-foreground", children: [
          "Last rename: ",
          d,
          " item",
          d === 1 ? "" : "s",
          " renamed · ",
          jn(N)
        ] }),
        /* @__PURE__ */ s(
          G,
          {
            variant: "ghost",
            onClick: () => {
              m(!0);
            },
            disabled: p,
            children: [
              /* @__PURE__ */ n(jt, { className: "h-4 w-4" }),
              "Undo last rename"
            ]
          }
        )
      ] }),
      u ? /* @__PURE__ */ n(I, { kind: u.kind, children: u.text }) : null
    ] }) : /* @__PURE__ */ s("div", { className: "space-y-2", children: [
      /* @__PURE__ */ n("span", { className: "text-sm text-secondary", children: "No rename to undo." }),
      u ? /* @__PURE__ */ n("div", { children: /* @__PURE__ */ n(I, { kind: u.kind, children: u.text }) }) : null
    ] }),
    h ? /* @__PURE__ */ s(
      Tt,
      {
        titleId: et,
        describedById: tt,
        pending: p,
        onCancel: () => {
          m(!1);
        },
        size: "sm",
        children: [
          /* @__PURE__ */ n("h2", { id: et, className: "mb-2 text-lg font-semibold text-foreground", children: "Undo last rename?" }),
          /* @__PURE__ */ s("p", { id: tt, className: "mb-6 text-sm text-secondary", children: [
            "This moves ",
            d,
            " file",
            d === 1 ? "" : "s",
            " back to their original names. This can't be undone again."
          ] }),
          /* @__PURE__ */ s("div", { className: "flex justify-end gap-3", children: [
            /* @__PURE__ */ n(
              "button",
              {
                type: "button",
                onClick: () => {
                  m(!1);
                },
                disabled: p,
                className: "px-4 py-2 text-sm text-secondary hover:text-foreground disabled:opacity-60",
                children: "Cancel"
              }
            ),
            /* @__PURE__ */ s(
              "button",
              {
                type: "button",
                onClick: () => void z(),
                disabled: p,
                className: "inline-flex items-center gap-2 rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-500 disabled:opacity-60",
                children: [
                  p ? /* @__PURE__ */ n(ee, {}) : null,
                  "Undo ",
                  d,
                  " rename",
                  d === 1 ? "" : "s"
                ]
              }
            )
          ] })
        ]
      }
    ) : null
  ] });
}
const Kn = {
  amber: "border-amber-400/40 bg-amber-400/10 text-amber-400",
  gray: "border-border bg-card text-muted",
  red: "border-red-700/50 bg-red-950/40 text-red-400"
};
function qn(t) {
  const e = [];
  switch (t.status) {
    case "NoOp":
      e.push({ label: "No change needed", variant: "gray" });
      break;
    case "SkipGated":
      e.push({ label: "Skipped — needs a required field", variant: "amber" });
      break;
    case "SkipCollision":
      e.push({ label: "Skipped — name conflict", variant: "amber" });
      break;
    case "SkipLocked":
      e.push({ label: "Skipped — file in use", variant: "amber" });
      break;
    case "Failed":
      e.push({ label: "Failed — rolled back", variant: "red" });
      break;
    case "Renamer":
    case "Move":
      t.suffixed && e.push({ label: "Numbered to avoid a clash", variant: "amber" }), t.sanitized && e.push({ label: "Cleaned for the filesystem", variant: "amber" });
      break;
  }
  return e;
}
function Gn({ badge: t }) {
  const e = t.variant === "amber" || t.variant === "red";
  return /* @__PURE__ */ s(
    "span",
    {
      className: `inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs font-medium ${Kn[t.variant]}`,
      children: [
        e ? /* @__PURE__ */ n(Ce, { className: "h-3 w-3" }) : null,
        t.label
      ]
    }
  );
}
function Wn({ item: t }) {
  const e = qn(t);
  return e.length === 0 ? null : /* @__PURE__ */ n("span", { className: "inline-flex flex-wrap gap-1", children: e.map((a) => /* @__PURE__ */ n(Gn, { badge: a }, a.label)) });
}
const Hn = /* @__PURE__ */ new Set([
  "SkipGated",
  "SkipCollision",
  "SkipLocked",
  "SkipBlocked",
  "SkipNoSpace",
  "SkipExcluded",
  "Failed"
]);
function Rt(t) {
  let e = 0, a = 0;
  for (const l of t)
    l.status === "Renamer" || l.status === "Move" ? e++ : Hn.has(l.status) && a++;
  return { renamed: e, skipped: a, scanned: t.length };
}
function Vn(t, e, a = 50) {
  return t.slice(e * a, e * a + a);
}
function Jn(t, e = 50) {
  return Math.max(1, Math.ceil(t / e));
}
const $t = "com.alextomas955.renamer", Yn = `/extensions/${$t}/scan-library`, Xn = `/extensions/${$t}/last-scan`, at = "rename-dry-run-title", rt = "rename-dry-run-summary", lt = 50, Zn = 1e3;
function ot(t) {
  return t instanceof Q ? `${t.status} ${t.body}` : String(t);
}
function st(t) {
  if (!t) return t;
  const e = Math.max(t.lastIndexOf("/"), t.lastIndexOf("\\"));
  return e >= 0 ? t.slice(e + 1) : t;
}
function Qn(t, e) {
  V(() => {
    if (!t) return;
    let a = !1;
    const l = setInterval(() => {
      A(`/jobs/${t}`).then((o) => {
        a || (o.status === "completed" || o.status === "failed" || o.status === "cancelled") && (clearInterval(l), e(o));
      }).catch(() => {
      });
    }, Zn);
    return () => {
      a = !0, clearInterval(l);
    };
  }, [t]);
}
function ea({
  onClose: t,
  onRenameAll: e,
  renaming: a
}) {
  const [l, o] = k(null), [i, c] = k(null), [h, m] = k(null), [p, g] = k(0), u = H(!1);
  V(() => {
    u.current || (u.current = !0, A(Yn, { method: "POST" }).then((d) => {
      o(d.jobId);
    }).catch((d) => {
      m(ot(d));
    }));
  }, []), Qn(l, (d) => {
    if (d.status !== "completed") {
      m(d.error ?? "the scan job did not complete");
      return;
    }
    A(Xn).then((N) => {
      c(N);
    }).catch((N) => {
      m(ot(N));
    });
  });
  const f = i ? Rt(i) : null, b = i ? Jn(i.length, lt) : 1, x = i ? Vn(i, p, lt) : [];
  return /* @__PURE__ */ s(
    Tt,
    {
      titleId: at,
      describedById: rt,
      pending: a,
      onCancel: t,
      size: "xl",
      children: [
        /* @__PURE__ */ n("h2", { id: at, className: "mb-2 text-lg font-semibold text-foreground", children: "Dry run" }),
        h ? /* @__PURE__ */ n("div", { className: "mb-4", children: /* @__PURE__ */ s(In, { children: [
          "Couldn't scan your library — ",
          h,
          ". Close and try again."
        ] }) }) : i === null || f === null ? /* @__PURE__ */ s("div", { className: "flex items-center gap-2 py-8 text-sm text-secondary", children: [
          /* @__PURE__ */ n(ee, {}),
          "Scanning your library…"
        ] }) : /* @__PURE__ */ s(Y, { children: [
          /* @__PURE__ */ s("p", { id: rt, className: "mb-4 text-sm text-secondary", children: [
            /* @__PURE__ */ n("span", { className: "text-foreground", children: f.renamed }),
            " will be renamed ·",
            " ",
            f.skipped,
            " skipped · ",
            f.scanned,
            " scanned"
          ] }),
          f.scanned === 0 ? /* @__PURE__ */ n("p", { className: "py-8 text-center text-sm text-secondary", children: "No items match your current settings — nothing to rename." }) : /* @__PURE__ */ n(Y, { children: /* @__PURE__ */ s("div", { className: "max-h-96 overflow-y-auto rounded border border-border text-sm", children: [
            /* @__PURE__ */ s("table", { className: "w-full border-collapse", children: [
              /* @__PURE__ */ n("thead", { children: /* @__PURE__ */ s("tr", { className: "sticky top-0 bg-card text-left", children: [
                /* @__PURE__ */ n("th", { className: "w-20 px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted", children: "Type" }),
                /* @__PURE__ */ n("th", { className: "min-w-0 flex-1 px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted", children: "Current name" }),
                /* @__PURE__ */ n("th", { className: "min-w-0 flex-1 px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted", children: "New name" }),
                /* @__PURE__ */ n("th", { className: "min-w-0 flex-1 px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted", children: "Destination" }),
                /* @__PURE__ */ n("th", { className: "px-3 py-2" })
              ] }) }),
              /* @__PURE__ */ n("tbody", { className: "divide-y divide-border", children: x.map((d) => {
                const N = d.status !== "Renamer" && d.status !== "Move", z = st(d.oldFullPath), R = d.newBasename || st(d.newFullPath);
                return /* @__PURE__ */ s("tr", { className: N ? "opacity-70" : void 0, children: [
                  /* @__PURE__ */ n("td", { className: "w-20 px-3 py-2 text-sm text-secondary", children: d.kind }),
                  /* @__PURE__ */ n(
                    "td",
                    {
                      className: "min-w-0 max-w-0 truncate px-3 py-2 font-mono text-sm text-muted",
                      title: d.oldFullPath,
                      children: z
                    }
                  ),
                  /* @__PURE__ */ n(
                    "td",
                    {
                      className: `min-w-0 max-w-0 truncate px-3 py-2 font-mono text-sm ${N ? "text-muted" : "text-foreground"}`,
                      title: d.newFullPath,
                      children: N ? "— will be skipped" : R
                    }
                  ),
                  /* @__PURE__ */ n(
                    "td",
                    {
                      className: "min-w-0 max-w-0 truncate px-3 py-2 font-mono text-xs text-muted",
                      title: d.targetFolderPath,
                      children: d.targetFolderPath
                    }
                  ),
                  /* @__PURE__ */ n("td", { className: "px-3 py-2", children: /* @__PURE__ */ n(Wn, { item: d }) })
                ] }, d.fileId);
              }) })
            ] }),
            /* @__PURE__ */ s("div", { className: "flex items-center justify-between border-t border-border bg-card px-3 py-2", children: [
              /* @__PURE__ */ n(
                G,
                {
                  variant: "ghost",
                  onClick: () => {
                    g((d) => d - 1);
                  },
                  disabled: p === 0,
                  children: "Prev"
                }
              ),
              /* @__PURE__ */ s("span", { className: "text-xs text-muted", children: [
                "Page ",
                p + 1,
                " of ",
                b
              ] }),
              /* @__PURE__ */ n(
                G,
                {
                  variant: "ghost",
                  onClick: () => {
                    g((d) => d + 1);
                  },
                  disabled: p === b - 1,
                  children: "Next"
                }
              )
            ] })
          ] }) })
        ] }),
        /* @__PURE__ */ s("div", { className: "mt-6 flex justify-end gap-3", children: [
          /* @__PURE__ */ n(G, { variant: "ghost", onClick: t, disabled: a, children: "Close" }),
          /* @__PURE__ */ s(
            G,
            {
              onClick: () => {
                i && e(i);
              },
              disabled: a || !f || f.renamed === 0,
              children: [
                a ? /* @__PURE__ */ n(ee, {}) : null,
                "Rename ",
                (f == null ? void 0 : f.renamed) ?? 0,
                " files"
              ]
            }
          )
        ] })
      ]
    }
  );
}
const Ue = new Set(Me.map((t) => t.token.slice(1).toLowerCase())), it = Me.map((t) => t.token.slice(1));
function ta(t) {
  const e = t.startsWith("$") ? t.slice(1) : t;
  return Ue.has(e.toLowerCase());
}
function na(t) {
  let e = 0;
  for (const a of t)
    if (a === "{") e++;
    else if (a === "}" && (e--, e < 0))
      return !1;
  return e === 0;
}
function aa(t) {
  const e = [], a = /* @__PURE__ */ new Set();
  for (let l = 0; l < t.length; l++) {
    if (t[l] !== "$") continue;
    if (t[l + 1] === "$") {
      l++;
      continue;
    }
    let o = l + 1;
    for (; o < t.length && /[A-Za-z0-9_]/.test(t[o]); ) o++;
    if (o === l + 1) continue;
    const i = t.slice(l + 1, o), c = i.toLowerCase();
    !Ue.has(c) && !a.has(c) && (a.add(c), e.push(`$${i}`)), l = o - 1;
  }
  return e;
}
function ct(t, e) {
  for (let a = 0; a < t.length; a++) {
    if (t[a] !== "$") continue;
    if (t[a + 1] === "$") {
      a++;
      continue;
    }
    let l = a + 1;
    for (; l < t.length && /[A-Za-z0-9_]/.test(t[l]); ) l++;
    if (l !== a + 1) {
      if (t.slice(a + 1, l).toLowerCase() === e) return !0;
      a = l - 1;
    }
  }
  return !1;
}
function we(t, e, a) {
  const l = (t.startsWith("$") ? t.slice(1) : t).toLowerCase();
  return ct(e, l) || ct(a, l);
}
function ra(t, e) {
  const a = t.length, l = e.length, o = Array.from({ length: l + 1 }, (i, c) => c);
  for (let i = 1; i <= a; i++) {
    let c = o[0];
    o[0] = i;
    for (let h = 1; h <= l; h++) {
      const m = o[h];
      o[h] = t[i - 1] === e[h - 1] ? c : Math.min(c, o[h - 1], o[h]) + 1, c = m;
    }
  }
  return o[l];
}
function At(t) {
  const e = (t.startsWith("$") ? t.slice(1) : t).toLowerCase();
  let a, l = 1 / 0;
  for (const o of Ue) {
    const i = ra(e, o);
    i < l && (l = i, a = o);
  }
  return a !== void 0 && l > 0 && l <= 2 ? `$${a}` : void 0;
}
const la = [
  // The shipped default, offered as a chip so a user who edits the template can return to it in one
  // click. The string matches DEFAULT_OPTIONS.FilenameTemplate exactly so the chip and "Reset to
  // defaults" produce the identical template.
  { label: "Date – Title [Height]", filenameTemplate: "{$date - }$title{ [$height]}" },
  { label: "Title + resolution", filenameTemplate: "$title{ [$resolution]}" },
  { label: "Studio – Title [Res]", filenameTemplate: "$studio{ - $title}{ [$resolution]}" },
  { label: "Date – Title", filenameTemplate: "$date{ - $title}" },
  { label: "Performers – Title", filenameTemplate: "$performers{ - $title}" }
], me = "com.alextomas955.renamer", Dt = "options", oa = `/extensions/${me}/data`, sa = `/extensions/${me}/preview-sample`, ia = `/extensions/${me}/renamer-library`, ca = 250, da = 1e3;
function dt(t) {
  let e = t.trim();
  return e.startsWith(".") && (e = e.slice(1)), e.toLowerCase();
}
const ua = [
  { value: "None", label: "None" },
  { value: "Lower", label: "lower case" },
  { value: "Title", label: "Title Case" }
], ut = [
  { value: "DropAll", label: "Drop all when over the max" },
  { value: "KeepFirst", label: "Keep the first N" }
], ma = [
  { value: "NameAsc", label: "Name (A→Z)" },
  { value: "None", label: "Keep original order" },
  { value: "IdAsc", label: "By internal id" },
  { value: "FavoriteFirst", label: "Favorites first, then name" }
], ha = [
  { value: "NameAsc", label: "Name (A→Z)" },
  { value: "None", label: "Keep original order" }
], mt = [
  { value: "Male", label: "Male" },
  { value: "Female", label: "Female" },
  { value: "TransgenderMale", label: "Transgender male" },
  { value: "TransgenderFemale", label: "Transgender female" },
  { value: "Intersex", label: "Intersex" },
  { value: "NonBinary", label: "Non-binary" }
], Te = [
  "title",
  "studio",
  "parentStudio",
  "studioCode",
  "director",
  "bitrate",
  "date",
  "year",
  "height",
  "width",
  "resolution",
  "videoCodec",
  "audioCodec",
  "frameRate",
  "duration",
  "performers",
  "tags",
  "ext"
].map((t) => ({ value: t, label: t })), pa = [
  { value: "yyyy-MM-dd", example: "2026-03-12" },
  { value: "yyyy", example: "2026" },
  { value: "MM-dd-yyyy", example: "03-12-2026" },
  { value: "dd.MM.yyyy", example: "12.03.2026" },
  { value: "yyyy.MM.dd", example: "2026.03.12" }
], fa = [
  { value: "hh\\-mm\\-ss", example: "01-23-45" },
  { value: "hh\\.mm\\.ss", example: "01.23.45" },
  { value: "mm\\-ss", example: "83-45" }
], ht = [
  { value: ", ", label: "Comma + space ( , )" },
  { value: " · ", label: "Middot ( · )" },
  { value: " ", label: "Space ( ␣ )" },
  { value: " - ", label: "Dash ( - )" }
], ga = [
  { value: " ({n})", example: "name (1).mp4" },
  { value: "_{n}", example: "name_1.mp4" },
  { value: " - {n}", example: "name - 1.mp4" }
];
function pt({
  value: t,
  emptySamples: e = []
}) {
  const a = [];
  na(t) || a.push("Unmatched { or } — it'll still render, but check your groups.");
  for (const l of aa(t)) {
    const o = At(l);
    a.push(
      o ? `${l} isn't a known token — it'll render as empty. Did you mean ${o}?` : `${l} isn't a known token — it'll render as empty.`
    );
  }
  for (const l of e)
    a.push(`This template produces an empty name for the "${l}" sample.`);
  return a.length === 0 ? null : /* @__PURE__ */ n("div", { className: "mt-1 space-y-1", role: "status", "aria-live": "polite", children: a.map((l) => /* @__PURE__ */ s("p", { className: "flex items-start gap-1 text-xs text-amber-400", children: [
    /* @__PURE__ */ n(Ce, { className: "h-3 w-3 shrink-0" }),
    /* @__PURE__ */ n("span", { children: l })
  ] }, l)) });
}
function ft({ values: t }) {
  const e = [];
  for (const a of t) {
    if (ta(a)) continue;
    const l = At(a), o = l ? l.slice(1) : void 0;
    e.push(
      o ? `"${a}" isn't a known token — it'll be ignored. Did you mean ${o}?` : `"${a}" isn't a known token — it'll be ignored.`
    );
  }
  return e.length === 0 ? null : /* @__PURE__ */ n("div", { className: "mt-1 space-y-1", role: "status", "aria-live": "polite", children: e.map((a) => /* @__PURE__ */ s("p", { className: "flex items-start gap-1 text-xs text-amber-400", children: [
    /* @__PURE__ */ n(Ce, { className: "h-3 w-3 shrink-0" }),
    /* @__PURE__ */ n("span", { children: a })
  ] }, a)) });
}
function ba({ onApply: t }) {
  return /* @__PURE__ */ s("div", { children: [
    /* @__PURE__ */ n("span", { className: "mb-1 block text-xs font-medium uppercase tracking-wide text-muted", children: "Presets" }),
    /* @__PURE__ */ n("div", { className: "flex flex-wrap gap-1", children: la.map((e) => /* @__PURE__ */ n(
      W,
      {
        selected: !1,
        title: e.filenameTemplate,
        onClick: () => {
          t(e.filenameTemplate);
        },
        children: e.label
      },
      e.label
    )) }),
    /* @__PURE__ */ n("p", { className: "mt-1 text-xs text-muted", children: "Click a preset to fill the filename template. You can edit it afterwards." })
  ] });
}
function xa({
  dirty: t,
  saving: e,
  saveError: a,
  savedFlash: l,
  canSave: o,
  onSave: i,
  onDiscard: c
}) {
  return t ? /* @__PURE__ */ n("div", { className: "fixed inset-x-0 bottom-0 z-50 border-t border-border bg-surface px-6 py-4", children: /* @__PURE__ */ s("div", { className: "flex items-center gap-3", children: [
    a ? /* @__PURE__ */ s(I, { kind: "error", children: [
      "Couldn't save settings — ",
      a,
      ". Your changes are still here; try Save again."
    ] }) : l ? /* @__PURE__ */ n(I, { kind: "success", children: "Settings saved." }) : /* @__PURE__ */ n(I, { kind: "muted", children: "Unsaved changes" }),
    /* @__PURE__ */ s("div", { className: "ml-auto flex items-center gap-3", children: [
      /* @__PURE__ */ n(G, { variant: "ghost", onClick: c, disabled: e, children: "Discard" }),
      /* @__PURE__ */ s(G, { onClick: i, disabled: !o || e, children: [
        e ? /* @__PURE__ */ n(ee, {}) : null,
        "Save changes"
      ] })
    ] })
  ] }) }) : null;
}
async function va(t, e) {
  const a = { ...e, ...t };
  try {
    await A(`${oa}/${Dt}`, {
      method: "PUT",
      // Double-encode: inner serialize = the stored value; outer serialize makes it a JSON
      // string literal for the [FromBody] string binder.
      body: JSON.stringify(JSON.stringify(a))
    });
  } catch (l) {
    if (l instanceof Q) throw l;
  }
}
function ya() {
  const t = qt(me), [e, a] = k(() => xe()), [l, o] = k(() => xe()), [i, c] = k(!0), [h, m] = k(null), [p, g] = k(!1), [u, f] = k(null), [b, x] = k(!1), [d, N] = k(!1), [z, R] = k(!1), [Z, te] = k(""), S = H({}), [K, ne] = k(null), [ie, ae] = k(!1), re = H(null), le = H(null), D = H("filename"), L = JSON.stringify(e) !== JSON.stringify(l), C = L || z, [J, ce] = k(!1), [Ee, Be] = k(!1), [pe, Re] = k(null);
  function je(r) {
    return new Promise((E, y) => {
      const T = setInterval(() => {
        A(`/jobs/${r}`).then(($) => {
          $.status === "completed" ? (clearInterval(T), E()) : ($.status === "failed" || $.status === "cancelled") && (clearInterval(T), y(new Error($.error ?? "the job did not complete")));
        }).catch(() => {
        });
      }, da);
    });
  }
  const ze = ye(async (r) => {
    Be(!0), Re(null);
    try {
      let E = r;
      if (!E) {
        const { jobId: $ } = await A(
          `/extensions/${me}/scan-library`,
          { method: "POST" }
        );
        await je($), E = await A(`/extensions/${me}/last-scan`);
      }
      const y = Rt(E), { jobId: T } = await A(ia, { method: "POST" });
      await je(T), ce(!1), Re({
        kind: "success",
        text: `Renamed ${y.renamed} file${y.renamed === 1 ? "" : "s"}` + (y.skipped > 0 ? `, ${y.skipped} skipped` : "") + "."
      });
    } catch (E) {
      const y = E instanceof Q ? `${E.status} ${E.body}` : String(E);
      Re({
        kind: "error",
        text: `Couldn't rename — ${y}. Nothing was changed; you can try again.`
      });
    } finally {
      Be(!1);
    }
  }, []), $e = ye(async () => {
    c(!0), m(null), R(!1);
    try {
      const E = (await t.getAll())[Dt];
      if (E) {
        N(!1);
        let y;
        try {
          y = JSON.parse(E);
        } catch {
          S.current = {};
          const de = xe();
          a(de), o(de), R(!0);
          return;
        }
        S.current = tn(y);
        const T = nn(y), $ = {
          ...T,
          EnableStudioDestinations: T.EnableStudioDestinations || Object.keys(T.StudioDestinations).length > 0,
          EnableTagDestinations: T.EnableTagDestinations || Object.keys(T.TagDestinations).length > 0,
          EnableAdvancedRouting: T.EnableAdvancedRouting || T.AllowedRoots.length > 0 || T.PathDestinations.length > 0
        };
        a($), o($);
      } else {
        N(!0), S.current = {};
        const y = xe();
        a(y), o(y);
      }
    } catch (r) {
      m(r instanceof Q ? `${r.status} ${r.body}` : String(r));
    } finally {
      c(!1);
    }
  }, [t]);
  V(() => {
    $e();
  }, [$e]), V(() => {
    if (i) return;
    const r = setTimeout(() => {
      A(sa, {
        method: "POST",
        body: JSON.stringify({ Options: e })
      }).then((E) => {
        ne(E), ae(!1);
      }).catch(() => {
        ae(!0);
      });
    }, ca);
    return () => {
      clearTimeout(r);
    };
  }, [e, i]);
  async function Ot() {
    g(!0), f(null);
    try {
      await va(e, S.current), o(e), N(!1), R(!1), x(!0), setTimeout(() => {
        x(!1);
      }, 3e3);
    } catch (r) {
      f(r instanceof Q ? `${r.status} ${r.body}` : String(r));
    } finally {
      g(!1);
    }
  }
  function v(r, E) {
    a((y) => ({ ...y, [r]: E }));
  }
  function B(r, E) {
    a((y) => ({ ...y, [r]: { ...y[r], ...E } }));
  }
  function fe(r) {
    const E = D.current, y = E === "folder" ? le.current : re.current, T = E === "folder" ? "FolderTemplate" : "FilenameTemplate", $ = e[T];
    if (y && typeof y.selectionStart == "number") {
      const de = y.selectionStart, It = y.selectionEnd ?? de, Lt = $.slice(0, de) + r + $.slice(It);
      v(T, Lt), requestAnimationFrame(() => {
        y.focus();
        const Ge = de + r.length;
        y.setSelectionRange(Ge, Ge);
      });
    } else
      v(T, $ + r);
  }
  if (i)
    return /* @__PURE__ */ s("div", { className: "flex items-center gap-2 text-sm text-secondary", children: [
      /* @__PURE__ */ n(ee, {}),
      "Loading settings…"
    ] });
  if (h)
    return /* @__PURE__ */ s("div", { className: "space-y-3", children: [
      /* @__PURE__ */ s(I, { kind: "error", children: [
        "Couldn't load your saved settings — ",
        h,
        ". Retry, or continue with defaults below."
      ] }),
      /* @__PURE__ */ n("div", { children: /* @__PURE__ */ n(G, { variant: "ghost", onClick: () => void $e(), children: "Retry" }) })
    ] });
  const j = (r) => e[r], Ft = (K ?? []).filter((r) => r.flags.includes("empty")).map((r) => r.sampleLabel), Ke = we(
    "performers",
    e.FilenameTemplate,
    e.FolderTemplate
  ), qe = we("tags", e.FilenameTemplate, e.FolderTemplate), oe = we("date", e.FilenameTemplate, e.FolderTemplate), ge = we(
    "duration",
    e.FilenameTemplate,
    e.FolderTemplate
  );
  return /* @__PURE__ */ s("div", { className: `space-y-6 ${L ? "pb-20" : ""}`, children: [
    /* @__PURE__ */ s("div", { className: "grid grid-cols-1 gap-6 lg:grid-cols-3", children: [
      /* @__PURE__ */ s("div", { className: "col-span-2", children: [
        z ? /* @__PURE__ */ n(I, { kind: "error", children: "Your saved settings couldn't be read and have been reset to defaults. Review the options below and save to store a clean copy." }) : d ? /* @__PURE__ */ n(I, { kind: "muted", children: "Using default settings — pick a preset or write a template, then save." }) : null,
        /* @__PURE__ */ s(F, { title: "Essentials", defaultOpen: !0, children: [
          /* @__PURE__ */ n(
            ba,
            {
              onApply: (r) => {
                v("FilenameTemplate", r);
              }
            }
          ),
          /* @__PURE__ */ n(w, { label: "Filename template", children: /* @__PURE__ */ n(
            U,
            {
              value: e.FilenameTemplate,
              onChange: (r) => {
                v("FilenameTemplate", r);
              },
              onFocus: () => D.current = "filename",
              inputRef: re,
              mono: !0,
              placeholder: "$title"
            }
          ) }),
          /* @__PURE__ */ n(pt, { value: e.FilenameTemplate, emptySamples: Ft }),
          /* @__PURE__ */ n(Pn, { onInsert: fe }),
          /* @__PURE__ */ n(
            w,
            {
              label: "Folder template",
              helper: "Blank = no folder move (rename in place). Use / for sub-folders, e.g. $studio / $year.",
              children: /* @__PURE__ */ n(
                U,
                {
                  value: e.FolderTemplate,
                  onChange: (r) => {
                    v("FolderTemplate", r);
                  },
                  onFocus: () => D.current = "folder",
                  inputRef: le,
                  mono: !0,
                  placeholder: "$studio / $year"
                }
              )
            }
          ),
          /* @__PURE__ */ n(pt, { value: e.FolderTemplate })
        ] })
      ] }),
      /* @__PURE__ */ n("div", { children: /* @__PURE__ */ s("div", { className: "space-y-4 lg:sticky lg:top-16", children: [
        /* @__PURE__ */ n("div", { className: "text-base font-semibold text-foreground", children: "Live preview" }),
        /* @__PURE__ */ n("p", { className: "mb-4 mt-1 text-sm text-secondary", children: "Old → new for sample items, before anything touches disk." }),
        ie ? /* @__PURE__ */ n(I, { kind: "error", children: "Preview unavailable — saved naming still works." }) : null,
        K == null ? /* @__PURE__ */ s("div", { className: "flex items-center gap-2 text-sm text-secondary", children: [
          /* @__PURE__ */ n(ee, {}),
          "Rendering preview…"
        ] }) : /* @__PURE__ */ n("div", { className: "space-y-3", children: K.map((r) => /* @__PURE__ */ n(Fn, { result: r }, r.sampleLabel)) })
      ] }) })
    ] }),
    /* @__PURE__ */ s(F, { title: "What Gets Renamed", defaultOpen: !0, children: [
      /* @__PURE__ */ n(
        _,
        {
          label: "Only rename organized items",
          checked: e.OnlyOrganized,
          onChange: (r) => {
            v("OnlyOrganized", r);
          },
          helper: "Only rename items you've marked Organized — skips un-curated items so they don't get junk names."
        }
      ),
      /* @__PURE__ */ n(
        _,
        {
          label: "Use filename as title when none is set",
          checked: e.FilenameAsTitle,
          onChange: (r) => {
            v("FilenameAsTitle", r);
          },
          helper: "When an item has no title, use its current filename (without extension) as the title."
        }
      ),
      /* @__PURE__ */ s(
        w,
        {
          label: "Required fields",
          helper: "Items whose listed tokens resolve to nothing are skipped instead of renamed. Default: title.",
          children: [
            /* @__PURE__ */ n(
              be,
              {
                values: e.RequiredFields,
                onChange: (r) => {
                  v("RequiredFields", r);
                },
                placeholder: "Add token, press Enter"
              }
            ),
            /* @__PURE__ */ n(
              Je,
              {
                tokens: it,
                values: e.RequiredFields,
                onAdd: (r) => {
                  v(
                    "RequiredFields",
                    e.RequiredFields.includes(r) ? e.RequiredFields : [...e.RequiredFields, r]
                  );
                }
              }
            ),
            /* @__PURE__ */ n(ft, { values: e.RequiredFields })
          ]
        }
      )
    ] }),
    /* @__PURE__ */ s(F, { title: "Run & Automation", defaultOpen: !0, children: [
      /* @__PURE__ */ n(
        F,
        {
          title: "Automation",
          summary: "Auto-rename when an item's metadata changes",
          children: /* @__PURE__ */ n(
            _,
            {
              label: "Auto-rename on update",
              checked: e.AutoRenamerOnUpdate,
              onChange: (r) => {
                v("AutoRenamerOnUpdate", r);
              },
              helper: "When on, renames a video or image automatically whenever its metadata changes (respects the gating rules above). Off by default."
            }
          )
        }
      ),
      /* @__PURE__ */ s(
        F,
        {
          title: "Run for the whole library",
          summary: "Preview or rename every matching item in your library",
          children: [
            /* @__PURE__ */ s("div", { className: "flex flex-wrap items-center gap-3", children: [
              /* @__PURE__ */ n(
                G,
                {
                  variant: "ghost",
                  onClick: () => {
                    ce(!0);
                  },
                  disabled: L,
                  children: "Dry run"
                }
              ),
              /* @__PURE__ */ s(G, { onClick: () => void ze(), disabled: L || Ee, children: [
                Ee ? /* @__PURE__ */ n(ee, {}) : null,
                "Rename all files"
              ] })
            ] }),
            L ? /* @__PURE__ */ s(
              "p",
              {
                className: "mt-2 flex items-start gap-1 text-xs text-amber-400",
                role: "status",
                "aria-live": "polite",
                children: [
                  /* @__PURE__ */ n(Ce, { className: "h-3 w-3 shrink-0" }),
                  /* @__PURE__ */ n("span", { children: "Save or discard your changes before running this." })
                ]
              }
            ) : null,
            pe ? /* @__PURE__ */ n("p", { className: "mt-2", children: /* @__PURE__ */ s(I, { kind: pe.kind, children: [
              pe.kind === "success" ? "✓ " : "",
              pe.text,
              pe.kind === "success" ? /* @__PURE__ */ s(Y, { children: [
                " ",
                /* @__PURE__ */ n(
                  "button",
                  {
                    type: "button",
                    onClick: () => {
                      var r;
                      (r = document.getElementById("rename-undo-section")) == null || r.scrollIntoView({ behavior: "smooth" });
                    },
                    className: "text-accent underline hover:no-underline",
                    children: "Undo"
                  }
                )
              ] }) : null
            ] }) }) : null
          ]
        }
      )
    ] }),
    J ? /* @__PURE__ */ n(
      ea,
      {
        onClose: () => {
          ce(!1);
        },
        onRenameAll: (r) => void ze(r),
        renaming: Ee
      }
    ) : null,
    /* @__PURE__ */ s(F, { title: "Token Settings", defaultOpen: !0, children: [
      Ke ? /* @__PURE__ */ s(
        F,
        {
          title: "Performers",
          summary: "Separators, limits, sort, and allow/block lists",
          children: [
            /* @__PURE__ */ n(w, { label: "Separator", children: /* @__PURE__ */ n(
              He,
              {
                value: j("Performers").Separator,
                onChange: (r) => {
                  B("Performers", { Separator: r });
                },
                options: ht,
                customPlaceholder: "Custom separator"
              }
            ) }),
            /* @__PURE__ */ n(w, { label: "Max count", helper: "0 = unlimited", children: /* @__PURE__ */ n(
              Ne,
              {
                value: j("Performers").MaxCount,
                min: 0,
                onChange: (r) => {
                  B("Performers", { MaxCount: r });
                }
              }
            ) }),
            /* @__PURE__ */ n(w, { label: "On overflow", children: /* @__PURE__ */ n(
              ue,
              {
                value: j("Performers").OnOverflow,
                onChange: (r) => {
                  B("Performers", { OnOverflow: r });
                },
                options: ut
              }
            ) }),
            /* @__PURE__ */ n(w, { label: "Sort", helper: "The id and favorite orders apply to performers only.", children: /* @__PURE__ */ n(
              ue,
              {
                value: j("Performers").Sort,
                onChange: (r) => {
                  B("Performers", { Sort: r });
                },
                options: ma
              }
            ) }),
            /* @__PURE__ */ n(
              w,
              {
                label: "Ignore genders",
                helper: "Drop performers of these genders before the max-count limit. A performer with no gender is always kept. None selected = off.",
                children: /* @__PURE__ */ n(
                  pn,
                  {
                    options: mt,
                    values: j("Performers").IgnoreGenders,
                    onChange: (r) => {
                      B("Performers", { IgnoreGenders: r });
                    }
                  }
                )
              }
            ),
            /* @__PURE__ */ n(
              w,
              {
                label: "Gender order",
                helper: "Preferred gender order, most-preferred first. Empty = off.",
                children: /* @__PURE__ */ n(
                  fn,
                  {
                    options: mt,
                    values: j("Performers").GenderOrder,
                    onChange: (r) => {
                      B("Performers", { GenderOrder: r });
                    },
                    addPrompt: "Add a gender…"
                  }
                )
              }
            ),
            /* @__PURE__ */ n(
              Ze,
              {
                label: "Whitelist",
                helper: "If set, only these performers are kept (case-insensitive).",
                values: j("Performers").Whitelist,
                onChange: (r) => {
                  B("Performers", { Whitelist: r });
                },
                placeholder: "Search performers…"
              }
            ),
            /* @__PURE__ */ n(
              Ze,
              {
                label: "Blacklist",
                helper: "These performers are removed (case-insensitive).",
                values: j("Performers").Blacklist,
                onChange: (r) => {
                  B("Performers", { Blacklist: r });
                },
                placeholder: "Search performers…"
              }
            )
          ]
        }
      ) : null,
      qe ? /* @__PURE__ */ s(
        F,
        {
          title: "Tags",
          summary: "Separators, limits, sort, and allow/block lists",
          children: [
            /* @__PURE__ */ n(w, { label: "Separator", children: /* @__PURE__ */ n(
              He,
              {
                value: j("Tags").Separator,
                onChange: (r) => {
                  B("Tags", { Separator: r });
                },
                options: ht,
                customPlaceholder: "Custom separator"
              }
            ) }),
            /* @__PURE__ */ n(w, { label: "Max count", helper: "0 = unlimited", children: /* @__PURE__ */ n(
              Ne,
              {
                value: j("Tags").MaxCount,
                min: 0,
                onChange: (r) => {
                  B("Tags", { MaxCount: r });
                }
              }
            ) }),
            /* @__PURE__ */ n(w, { label: "On overflow", children: /* @__PURE__ */ n(
              ue,
              {
                value: j("Tags").OnOverflow,
                onChange: (r) => {
                  B("Tags", { OnOverflow: r });
                },
                options: ut
              }
            ) }),
            /* @__PURE__ */ n(w, { label: "Sort", children: /* @__PURE__ */ n(
              ue,
              {
                value: j("Tags").Sort,
                onChange: (r) => {
                  B("Tags", { Sort: r });
                },
                options: ha
              }
            ) }),
            /* @__PURE__ */ n(
              Se,
              {
                label: "Whitelist",
                helper: "If set, only these tags are kept (case-insensitive).",
                values: j("Tags").Whitelist,
                onChange: (r) => {
                  B("Tags", { Whitelist: r });
                },
                placeholder: "Search tags…"
              }
            ),
            /* @__PURE__ */ n(
              Se,
              {
                label: "Blacklist",
                helper: "These tags are removed (case-insensitive).",
                values: j("Tags").Blacklist,
                onChange: (r) => {
                  B("Tags", { Blacklist: r });
                },
                placeholder: "Search tags…"
              }
            )
          ]
        }
      ) : null,
      oe || ge ? /* @__PURE__ */ s(
        F,
        {
          title: oe && ge ? "Date & duration format" : oe ? "Date format" : "Duration format",
          summary: oe && ge ? "How $date and $duration tokens are written" : oe ? "How the $date token is written" : "How the $duration token is written",
          children: [
            oe ? /* @__PURE__ */ n(w, { label: "Date format", helper: "e.g. yyyy-MM-dd", children: /* @__PURE__ */ n(
              De,
              {
                value: e.DateFormat,
                onChange: (r) => {
                  v("DateFormat", r);
                },
                options: pa,
                customPlaceholder: "yyyy-MM-dd"
              }
            ) }) : null,
            ge ? /* @__PURE__ */ n(w, { label: "Duration format", children: /* @__PURE__ */ n(
              De,
              {
                value: e.DurationFormat,
                onChange: (r) => {
                  v("DurationFormat", r);
                },
                options: fa,
                customPlaceholder: "hh\\-mm\\-ss"
              }
            ) }) : null
          ]
        }
      ) : null,
      !Ke && !qe && !oe && !ge ? /* @__PURE__ */ n(
        q,
        {
          title: "No token-specific settings needed",
          description: "Add $performers, $tags, $date, or $duration to your filename or folder template to configure how they're formatted.",
          children: /* @__PURE__ */ s("div", { className: "flex flex-wrap gap-1", children: [
            /* @__PURE__ */ n(
              W,
              {
                selected: !1,
                mono: !0,
                onClick: () => {
                  fe("{ - $performers}");
                },
                children: "$performers"
              }
            ),
            /* @__PURE__ */ n(
              W,
              {
                selected: !1,
                mono: !0,
                onClick: () => {
                  fe("{ - $tags}");
                },
                children: "$tags"
              }
            ),
            /* @__PURE__ */ n(
              W,
              {
                selected: !1,
                mono: !0,
                onClick: () => {
                  fe("{ - $date}");
                },
                children: "$date"
              }
            ),
            /* @__PURE__ */ n(
              W,
              {
                selected: !1,
                mono: !0,
                onClick: () => {
                  fe("{ [$duration]}");
                },
                children: "$duration"
              }
            )
          ] })
        }
      ) : null
    ] }),
    /* @__PURE__ */ n(F, { title: "Destination Routing", defaultOpen: !0, children: /* @__PURE__ */ s(
      F,
      {
        title: "Destination routing",
        summary: "Per-studio / tag / path destinations, allowed roots, and the default-relocate gate",
        children: [
          /* @__PURE__ */ n(
            q,
            {
              title: "Advanced routing & safety",
              headerRight: /* @__PURE__ */ n(
                _,
                {
                  label: "Enabled",
                  checked: e.EnableAdvancedRouting,
                  onChange: (r) => {
                    v("EnableAdvancedRouting", r);
                  }
                }
              ),
              children: e.EnableAdvancedRouting ? /* @__PURE__ */ s(Y, { children: [
                /* @__PURE__ */ n("h4", { className: "text-sm font-semibold text-foreground", children: "Allowed roots" }),
                /* @__PURE__ */ n("p", { className: "mb-4 mt-1 text-sm text-secondary", children: "A rename may only write inside these absolute directories; a target outside them is rejected. Empty = files stay within their own source folder." }),
                /* @__PURE__ */ n(
                  be,
                  {
                    values: e.AllowedRoots,
                    onChange: (r) => {
                      v("AllowedRoots", r);
                    },
                    placeholder: "Add an absolute directory, press Enter"
                  }
                ),
                /* @__PURE__ */ n("h4", { className: "text-sm font-semibold text-foreground", children: "Source-path destinations" }),
                /* @__PURE__ */ n("p", { className: "mb-4 mt-1 text-sm text-secondary", children: "Match an item's source path to a destination root, top rule first. An exact match or a regex." }),
                /* @__PURE__ */ n(
                  Pe,
                  {
                    rows: e.PathDestinations,
                    onChange: (r) => {
                      v("PathDestinations", r);
                    },
                    makeRow: () => ({ Pattern: "", Dest: "", IsRegex: !1 }),
                    renderRow: (r, E, y) => /* @__PURE__ */ s(Y, { children: [
                      /* @__PURE__ */ n(w, { label: "Source path", children: /* @__PURE__ */ n(
                        U,
                        {
                          value: r.Pattern,
                          onChange: (T) => {
                            y({ Pattern: T });
                          },
                          mono: !0,
                          placeholder: "Exact path or regex"
                        }
                      ) }),
                      /* @__PURE__ */ n(
                        _,
                        {
                          label: "Match as a regex",
                          checked: r.IsRegex,
                          onChange: (T) => {
                            y({ IsRegex: T });
                          }
                        }
                      ),
                      /* @__PURE__ */ n(Ye, { pattern: r.Pattern, isRegex: r.IsRegex }),
                      /* @__PURE__ */ s(w, { label: "Destination root", children: [
                        /* @__PURE__ */ n(
                          U,
                          {
                            value: r.Dest,
                            onChange: (T) => {
                              y({ Dest: T });
                            },
                            placeholder: "Destination root"
                          }
                        ),
                        /* @__PURE__ */ n(ve, { value: r.Dest })
                      ] })
                    ] }),
                    addLabel: "Add path rule",
                    ordered: !0
                  }
                )
              ] }) : /* @__PURE__ */ n("p", { className: "text-sm text-secondary", children: "Turn this on to add advanced routing rules." })
            }
          ),
          /* @__PURE__ */ n(
            q,
            {
              title: "Per-studio destinations",
              description: "Pick a studio, then the absolute root its items route to.",
              headerRight: /* @__PURE__ */ n(
                _,
                {
                  label: "Enabled",
                  checked: e.EnableStudioDestinations,
                  onChange: (r) => {
                    v("EnableStudioDestinations", r);
                  }
                }
              ),
              children: e.EnableStudioDestinations ? /* @__PURE__ */ n(
                $n,
                {
                  map: e.StudioDestinations,
                  onChange: (r) => {
                    v("StudioDestinations", r);
                  }
                }
              ) : /* @__PURE__ */ n("p", { className: "text-sm text-secondary", children: "Turn this on to add per-studio routing rules." })
            }
          ),
          /* @__PURE__ */ n(
            q,
            {
              title: "Per-tag destinations",
              description: "Pick a tag, then the absolute root its items route to.",
              headerRight: /* @__PURE__ */ n(
                _,
                {
                  label: "Enabled",
                  checked: e.EnableTagDestinations,
                  onChange: (r) => {
                    v("EnableTagDestinations", r);
                  }
                }
              ),
              children: e.EnableTagDestinations ? /* @__PURE__ */ n(
                St,
                {
                  map: e.TagDestinations,
                  onChange: (r) => {
                    v("TagDestinations", r);
                  },
                  renderKey: (r, E, y) => /* @__PURE__ */ n(
                    Se,
                    {
                      label: "Tag",
                      values: r === "" ? [] : [r],
                      onChange: (T) => {
                        E(T.at(-1) ?? "");
                      },
                      placeholder: "Search tags…",
                      excludeValues: y
                    }
                  ),
                  renderValue: (r, E) => /* @__PURE__ */ s(Y, { children: [
                    /* @__PURE__ */ n(U, { value: r, onChange: E, placeholder: "Destination root" }),
                    /* @__PURE__ */ n(ve, { value: r })
                  ] }),
                  addLabel: "Add tag rule"
                }
              ) : /* @__PURE__ */ n("p", { className: "text-sm text-secondary", children: "Turn this on to add per-tag routing rules." })
            }
          ),
          /* @__PURE__ */ s(q, { title: "Default & unorganized destinations", children: [
            /* @__PURE__ */ s(
              w,
              {
                label: "Default destination",
                helper: "Where an item matching no rule goes. Blank = no default route. Honored only with the relocate gate below ON.",
                children: [
                  /* @__PURE__ */ n(
                    U,
                    {
                      value: e.DefaultDestination,
                      onChange: (r) => {
                        v("DefaultDestination", r);
                      },
                      placeholder: "Absolute root, or blank"
                    }
                  ),
                  /* @__PURE__ */ n(ve, { value: e.DefaultDestination })
                ]
              }
            ),
            /* @__PURE__ */ s(
              w,
              {
                label: "Unorganized destination",
                helper: "Where un-curated items route instead of being skipped. Blank = no unorganized route.",
                children: [
                  /* @__PURE__ */ n(
                    U,
                    {
                      value: e.UnorganizedDestination,
                      onChange: (r) => {
                        v("UnorganizedDestination", r);
                      },
                      placeholder: "Absolute root, or blank"
                    }
                  ),
                  /* @__PURE__ */ n(ve, { value: e.UnorganizedDestination })
                ]
              }
            ),
            /* @__PURE__ */ n(
              _,
              {
                label: "Relocate unmatched items to the default destination",
                checked: e.EnableDefaultRelocate,
                onChange: (r) => {
                  v("EnableDefaultRelocate", r);
                },
                helper: "With this on, any item matching no rule is moved to the default destination — whole-library reach. Undo is the only recovery. Off by default."
              }
            )
          ] }),
          /* @__PURE__ */ n(
            q,
            {
              title: "Sidecar files",
              description: "Files sharing the primary's basename with one of these extensions move and rename with it; a target that already exists is left untouched, never overwritten. Captions Cove tracks always move regardless.",
              children: /* @__PURE__ */ s(w, { label: "Also move sidecar files with these extensions", children: [
                /* @__PURE__ */ n(
                  be,
                  {
                    values: e.AssociatedExtensions,
                    onChange: (r) => {
                      v("AssociatedExtensions", r);
                    },
                    placeholder: "Add an extension, press Enter",
                    normalize: dt,
                    onReject: (r) => !/^[a-z0-9]+$/.test(r),
                    onLiveChange: (r) => {
                      te(r);
                    }
                  }
                ),
                (() => {
                  const r = on(
                    dt(Z)
                  );
                  return r ? /* @__PURE__ */ n(I, { kind: "warning", children: r }) : null;
                })()
              ] })
            }
          ),
          /* @__PURE__ */ n(q, { title: "Empty source folder", children: /* @__PURE__ */ n(
            _,
            {
              label: "Delete the source folder when a move leaves it empty",
              checked: e.RemoveEmptyFolder,
              onChange: (r) => {
                v("RemoveEmptyFolder", r);
              },
              helper: "Deletes a source folder only when a move empties it completely — never a non-empty folder or a root. Undo won't move the file back into a deleted folder; the file stays at its new location. Off by default."
            }
          ) })
        ]
      }
    ) }),
    /* @__PURE__ */ s(F, { title: "Advanced", defaultOpen: !0, children: [
      /* @__PURE__ */ s(
        F,
        {
          title: "Clean up the name",
          summary: "Illegal-character and space handling, case, ASCII",
          children: [
            /* @__PURE__ */ n(w, { label: "Illegal-char replacement", children: /* @__PURE__ */ n(
              Ve,
              {
                value: e.IllegalReplacement,
                onChange: (r) => {
                  v("IllegalReplacement", r);
                },
                stripLabel: "Strip",
                replaceLabel: "Replace with",
                stripHelper: "Illegal characters are removed.",
                replaceHelper: "Each illegal character becomes this.",
                inputPlaceholder: "e.g. _"
              }
            ) }),
            /* @__PURE__ */ n(w, { label: "Space replacement", children: /* @__PURE__ */ n(
              Ve,
              {
                value: e.SpaceReplacement,
                onChange: (r) => {
                  v("SpaceReplacement", r);
                },
                stripLabel: "Keep spaces",
                replaceLabel: "Replace with",
                stripHelper: "Spaces are left as-is.",
                replaceHelper: "Each space becomes this.",
                inputPlaceholder: "e.g. _ or ."
              }
            ) }),
            /* @__PURE__ */ n(
              w,
              {
                label: "Remove characters",
                helper: "Characters to delete from the name, e.g. ,# — separate from illegal-character handling.",
                children: /* @__PURE__ */ n(
                  U,
                  {
                    value: e.RemoveCharacters,
                    onChange: (r) => {
                      v("RemoveCharacters", r);
                    },
                    placeholder: "e.g. ,#"
                  }
                )
              }
            ),
            /* @__PURE__ */ n(w, { label: "Case", children: /* @__PURE__ */ n(
              ue,
              {
                value: e.Case,
                onChange: (r) => {
                  v("Case", r);
                },
                options: ua
              }
            ) }),
            /* @__PURE__ */ n(
              _,
              {
                label: "ASCII transliterate",
                checked: e.AsciiTransliterate,
                onChange: (r) => {
                  v("AsciiTransliterate", r);
                },
                helper: "Convert accented characters to plain ASCII."
              }
            )
          ]
        }
      ),
      /* @__PURE__ */ s(
        F,
        {
          title: "Length & collisions",
          summary: "Length caps, what to drop when too long, duplicate suffix",
          children: [
            /* @__PURE__ */ n(w, { label: "Filename max length", children: /* @__PURE__ */ n(
              Ne,
              {
                value: e.FilenameMax,
                min: 1,
                onChange: (r) => {
                  v("FilenameMax", r);
                }
              }
            ) }),
            /* @__PURE__ */ n(w, { label: "Full-path max length", children: /* @__PURE__ */ n(
              Ne,
              {
                value: e.FullPathMax,
                min: 1,
                onChange: (r) => {
                  v("FullPathMax", r);
                }
              }
            ) }),
            /* @__PURE__ */ s(w, { label: "Drop order", helper: "Fields dropped (top first) when the name is too long.", children: [
              /* @__PURE__ */ n(
                be,
                {
                  values: e.DropOrder,
                  onChange: (r) => {
                    v("DropOrder", r);
                  },
                  ordered: !0,
                  placeholder: "Add field, press Enter"
                }
              ),
              /* @__PURE__ */ n(
                Je,
                {
                  tokens: it,
                  values: e.DropOrder,
                  onAdd: (r) => {
                    v(
                      "DropOrder",
                      e.DropOrder.includes(r) ? e.DropOrder : [...e.DropOrder, r]
                    );
                  }
                }
              ),
              /* @__PURE__ */ n(ft, { values: e.DropOrder })
            ] }),
            /* @__PURE__ */ n(
              w,
              {
                label: "Duplicate suffix format",
                helper: "{n} = a counter added only when a name already exists, e.g. name (1).mp4.",
                children: /* @__PURE__ */ n(
                  De,
                  {
                    value: e.DuplicateSuffixFormat,
                    onChange: (r) => {
                      v("DuplicateSuffixFormat", r);
                    },
                    options: ga,
                    customPlaceholder: " ({n})"
                  }
                )
              }
            )
          ]
        }
      ),
      /* @__PURE__ */ s(
        F,
        {
          title: "Excludes",
          summary: "Skip items by tag, studio, or source path — evaluated before any routing",
          children: [
            /* @__PURE__ */ n(
              q,
              {
                title: "Exclude by tag",
                description: "An item carrying any of these tags is skipped — never renamed, never moved. Evaluated before any routing rule.",
                children: /* @__PURE__ */ n(
                  Se,
                  {
                    label: "Tags",
                    values: e.ExcludeTags,
                    onChange: (r) => {
                      v("ExcludeTags", r);
                    },
                    placeholder: "Search tags…"
                  }
                )
              }
            ),
            /* @__PURE__ */ n(
              q,
              {
                title: "Exclude by studio",
                description: "An item under any of these studios — or under a child of one — is skipped entirely. Evaluated before any routing rule.",
                children: /* @__PURE__ */ n(
                  wt,
                  {
                    label: "Studios",
                    values: e.ExcludeStudioIds,
                    onChange: (r) => {
                      v("ExcludeStudioIds", r);
                    },
                    placeholder: "Search studios…"
                  }
                )
              }
            ),
            /* @__PURE__ */ n(
              q,
              {
                title: "Exclude by source path",
                description: "An item whose source path matches a rule is skipped entirely. Evaluated before any routing rule. An exact match or a regex.",
                children: /* @__PURE__ */ n(
                  Pe,
                  {
                    rows: e.ExcludePaths,
                    onChange: (r) => {
                      v("ExcludePaths", r);
                    },
                    makeRow: () => ({ Pattern: "", IsRegex: !1 }),
                    renderRow: (r, E, y) => /* @__PURE__ */ s(Y, { children: [
                      /* @__PURE__ */ n(w, { label: "Source path", children: /* @__PURE__ */ n(
                        U,
                        {
                          value: r.Pattern,
                          onChange: (T) => {
                            y({ Pattern: T });
                          },
                          mono: !0,
                          placeholder: "Exact path or regex"
                        }
                      ) }),
                      /* @__PURE__ */ n(
                        _,
                        {
                          label: "Match as a regex",
                          checked: r.IsRegex,
                          onChange: (T) => {
                            y({ IsRegex: T });
                          }
                        }
                      ),
                      /* @__PURE__ */ n(Ye, { pattern: r.Pattern, isRegex: r.IsRegex })
                    ] }),
                    addLabel: "Add exclude rule"
                  }
                )
              }
            )
          ]
        }
      ),
      /* @__PURE__ */ s(
        F,
        {
          title: "Field rewriting",
          summary: "Literal token replacements, article stripping, name shaping, and per-token whitespace",
          children: [
            /* @__PURE__ */ n(
              q,
              {
                title: "Per-token replacements",
                description: "A literal find/replace on a single token's value, before the name is shaped. The target is a canonical token name (e.g. studio, title), matched case-insensitively.",
                children: /* @__PURE__ */ n(
                  Pe,
                  {
                    rows: e.FieldReplacers,
                    onChange: (r) => {
                      v("FieldReplacers", r);
                    },
                    makeRow: () => ({ TargetToken: Te[0].value, Find: "", Replace: "" }),
                    renderRow: (r, E, y) => {
                      const T = Te.some(($) => $.value === r.TargetToken) ? Te : [
                        ...Te,
                        { value: r.TargetToken, label: `${r.TargetToken} (unknown)` }
                      ];
                      return /* @__PURE__ */ s(Y, { children: [
                        /* @__PURE__ */ n(w, { label: "Target token", children: /* @__PURE__ */ n(
                          ue,
                          {
                            value: r.TargetToken,
                            onChange: ($) => {
                              y({ TargetToken: $ });
                            },
                            options: T
                          }
                        ) }),
                        /* @__PURE__ */ n(w, { label: "Find", helper: "Literal text to match. Empty does nothing.", children: /* @__PURE__ */ n(
                          U,
                          {
                            value: r.Find,
                            onChange: ($) => {
                              y({ Find: $ });
                            },
                            placeholder: "Text to find"
                          }
                        ) }),
                        /* @__PURE__ */ n(w, { label: "Replace with", children: /* @__PURE__ */ n(
                          U,
                          {
                            value: r.Replace,
                            onChange: ($) => {
                              y({ Replace: $ });
                            },
                            placeholder: "Replacement (blank to remove)"
                          }
                        ) })
                      ] });
                    },
                    addLabel: "Add replacement"
                  }
                )
              }
            ),
            /* @__PURE__ */ s(q, { title: "Strip leading article", children: [
              /* @__PURE__ */ n(
                _,
                {
                  label: "Strip a leading article from the title",
                  checked: e.StripLeadingArticles,
                  onChange: (r) => {
                    v("StripLeadingArticles", r);
                  },
                  helper: "Removes a single leading article and the whitespace after it from the title, at most once (case-insensitive) — a word merely starting with an article, and a mid-title article, are left alone."
                }
              ),
              /* @__PURE__ */ n(w, { label: "Articles", children: /* @__PURE__ */ n(
                be,
                {
                  values: e.Articles,
                  onChange: (r) => {
                    v("Articles", r);
                  },
                  placeholder: "Add article, press Enter"
                }
              ) })
            ] }),
            /* @__PURE__ */ s(q, { title: "Name shaping", children: [
              /* @__PURE__ */ n(
                _,
                {
                  label: "Squeeze studio names",
                  checked: e.SqueezeStudioNames,
                  onChange: (r) => {
                    v("SqueezeStudioNames", r);
                  },
                  helper: "Removes all spaces from the studio value so one studio renders to one stable folder name."
                }
              ),
              /* @__PURE__ */ n(
                _,
                {
                  label: "Drop a performer already in the title",
                  checked: e.PreventTitlePerformer,
                  onChange: (r) => {
                    v("PreventTitlePerformer", r);
                  },
                  helper: "Drops a performer whose name already appears as a whole word in the title."
                }
              ),
              /* @__PURE__ */ n(
                _,
                {
                  label: "Collapse repeated folder segments",
                  checked: e.PreventConsecutiveSegments,
                  onChange: (r) => {
                    v("PreventConsecutiveSegments", r);
                  },
                  helper: "Collapses consecutive duplicate folder path segments to one — affects the folder path, not the filename."
                }
              )
            ] })
          ]
        }
      )
    ] }),
    /* @__PURE__ */ n("div", { id: "rename-undo-section", children: /* @__PURE__ */ n(zn, { refreshKey: 0 }) }),
    /* @__PURE__ */ n(
      xa,
      {
        dirty: L,
        saving: p,
        saveError: u,
        savedFlash: b,
        canSave: C,
        onSave: () => void Ot(),
        onDiscard: () => {
          a(l);
        }
      }
    )
  ] });
}
function ka() {
  return /* @__PURE__ */ n(ya, {});
}
function gt(t) {
  if (!t) return t;
  const e = Math.max(t.lastIndexOf("/"), t.lastIndexOf("\\"));
  return e >= 0 ? t.slice(e + 1) : t;
}
const Na = 5;
function Sa(t) {
  const e = t / 1073741824;
  return e >= 10 ? `${Math.round(e)} GB` : `${e.toFixed(1)} GB`;
}
function wa(t) {
  return ((t == null ? void 0 : t.volumePairs) ?? []).map(
    (e) => `↪ ${e.count} item${e.count === 1 ? "" : "s"} (${Sa(e.bytes)}) move from ${e.from} to ${e.to}.`
  );
}
function Ta(t) {
  return t === "Heavy" ? "This is a LARGE cross-drive move — files will be COPIED across drives, which can take a while. Click OK only if you are sure; Cancel to stop. You can undo this afterwards." : t === "Standard" ? "This moves files across drives. Click OK to proceed, or Cancel to stop. You can undo this afterwards." : "Click OK to rename, or Cancel to stop. You can undo this afterwards.";
}
function Ca(t, e) {
  const a = t.filter((S) => S.status === "Renamer" || S.status === "Move"), l = a.length, o = t.length, i = t.filter((S) => S.status === "SkipGated").length, c = t.filter((S) => S.status === "SkipCollision").length, h = t.filter((S) => S.status === "SkipLocked").length, m = i + c + h, p = a.filter((S) => S.suffixed).length, g = a.filter((S) => S.sanitized).length, u = [];
  if (m > 0) {
    const S = [];
    if (i > 0 && S.push(`${i} need a required field`), c > 0 && S.push(`${c} have a name conflict`), h > 0 && S.push(`${h} are in use`), S.length === 1) {
      const K = i > 0 ? "needs a required field" : c > 0 ? "name conflict" : "in use";
      u.push(`⚠ ${m} skipped (${K}).`);
    } else
      u.push(`⚠ ${m} skipped — ${S.join(", ")}.`);
  }
  g > 0 && u.push(`⚠ ${g} had illegal characters cleaned up.`), p > 0 && u.push(`⚠ ${p} got a number added to avoid a name clash (e.g. "name (1)").`);
  const f = wa(e), b = u.length > 0 ? `${u.join(`
`)}

` : "", x = f.length > 0 ? `${f.join(`
`)}

` : "";
  if (l === 0)
    return { text: `Nothing will be renamed — all ${o} selected item${o === 1 ? "" : "s"} are skipped or already named correctly.

` + b + "Click OK to dismiss.", willRenameCount: 0 };
  const d = l === o ? `Rename ${l} selected item${l === 1 ? "" : "s"}?` : `Rename ${l} of ${o} selected items?`, N = a.slice(0, Na).map((S) => {
    const K = gt(S.oldFullPath), ne = S.newBasename || gt(S.newFullPath);
    return `  ${K}  →  ${ne}`;
  }), z = l - N.length;
  z > 0 && N.push(`  … and ${z} more.`);
  const R = (e == null ? void 0 : e.confirmLevel) ?? "Light", Z = Ta(R);
  return { text: `${d}

` + b + x + `Examples:
${N.join(`
`)}

` + Z, willRenameCount: l };
}
const Pt = "com.alextomas955.renamer", Ea = `/extensions/${Pt}/preview`, Ra = `/extensions/${Pt}/renamer`;
async function $a(t, e) {
  const a = JSON.stringify({ EntityType: e.entityType, EntityIds: e.entityIds }), l = await A(Ea, { method: "POST", body: a }), { text: o, willRenameCount: i } = Ca(l.items, l.summary);
  if (!window.confirm(o))
    return { cancelled: !0 };
  if (i === 0)
    return { cancelled: !0 };
  try {
    await A(Ra, { method: "POST", body: a });
  } catch (c) {
    if (c instanceof Q) throw c;
  }
  return {};
}
const Aa = { components: { RenamerPage: ka } };
Aa.actionHandlers = { renamerSelected: $a };
export {
  Aa as default
};
