var Ut = Object.defineProperty;
var Bt = (t, e, a) => e in t ? Ut(t, e, { enumerable: !0, configurable: !0, writable: !0, value: a }) : t[e] = a;
var Ne = (t, e, a) => Bt(t, typeof e != "symbol" ? e + "" : e, a);
import { useMemo as jt, useId as xt, useState as k, useRef as q, useEffect as G, useCallback as ke } from "react";
import { jsx as n, jsxs as s, Fragment as V } from "react/jsx-runtime";
import { Loader2 as zt, X as pe, ChevronUp as vt, ChevronDown as yt, Undo2 as Kt, AlertTriangle as Ee } from "lucide-react";
const qt = "/api";
async function A(t, e = {}) {
  const a = `${qt}${t}`, o = await fetch(a, {
    ...e,
    headers: {
      "Content-Type": "application/json",
      ...e.headers
    }
  });
  if (!o.ok) {
    const l = await o.text().catch(() => "");
    throw new Z(o.status, l || o.statusText, t);
  }
  if (o.status !== 204)
    return o.json();
}
class Z extends Error {
  constructor(a, o, l) {
    super(`API ${a} ${l}: ${o}`);
    Ne(this, "status");
    Ne(this, "body");
    Ne(this, "path");
    this.status = a, this.body = o, this.path = l, this.name = "ApiError";
  }
}
function Gt(t) {
  const e = `/extensions/${t}/data`;
  return {
    get: (a) => A(`${e}/${encodeURIComponent(a)}`).then((o) => o.value),
    set: (a, o) => A(e, { method: "POST", body: JSON.stringify({ key: a, value: o }) }),
    delete: (a) => A(`${e}/${encodeURIComponent(a)}`, { method: "DELETE" }),
    getAll: () => A(`${e}`)
  };
}
function Wt(t) {
  return jt(() => Gt(t), [t]);
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
function ve() {
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
function Fe(t, e) {
  return typeof t == "number" && Number.isFinite(t) ? t : e;
}
function _(t, e) {
  return typeof t == "boolean" ? t : e;
}
function J(t, e) {
  return Array.isArray(t) ? t.filter((a) => typeof a == "string") : e;
}
function Ht(t, e) {
  return Array.isArray(t) ? t.filter((a) => typeof a == "number" && Number.isFinite(a)) : e;
}
function Vt(t) {
  const e = Ie(t), a = {};
  for (const [o, l] of Object.entries(e)) {
    const i = Number(o);
    Number.isInteger(i) && typeof l == "string" && (a[i] = l);
  }
  return a;
}
function Jt(t) {
  const e = Ie(t), a = {};
  for (const [o, l] of Object.entries(e))
    typeof l == "string" && (a[o] = l);
  return a;
}
function Yt(t) {
  return Array.isArray(t) ? t.filter((e) => e && typeof e == "object").map((e) => {
    const a = e;
    return {
      Pattern: O(a.Pattern, ""),
      Dest: O(a.Dest, ""),
      IsRegex: _(a.IsRegex, !1)
    };
  }) : [];
}
function Xt(t) {
  return Array.isArray(t) ? t.filter((e) => e && typeof e == "object").map((e) => {
    const a = e;
    return { Pattern: O(a.Pattern, ""), IsRegex: _(a.IsRegex, !1) };
  }) : [];
}
function Zt(t) {
  return Array.isArray(t) ? t.filter((e) => e && typeof e == "object").map((e) => {
    const a = e;
    return {
      TargetToken: O(a.TargetToken, ""),
      Find: O(a.Find, ""),
      Replace: O(a.Replace, "")
    };
  }) : [];
}
function Qt(t) {
  return t === "KeepFirst" ? "KeepFirst" : "DropAll";
}
function en(t) {
  return t === "None" || t === "IdAsc" || t === "FavoriteFirst" ? t : "NameAsc";
}
function tn(t) {
  return t === "Lower" || t === "Title" ? t : "None";
}
function He(t, e) {
  const a = Ie(t);
  return {
    Separator: O(a.Separator, e.Separator),
    MaxCount: Fe(a.MaxCount, e.MaxCount),
    OnOverflow: Qt(a.OnOverflow),
    Sort: en(a.Sort),
    Whitelist: J(a.Whitelist, []),
    Blacklist: J(a.Blacklist, []),
    IgnoreGenders: J(a.IgnoreGenders, []),
    GenderOrder: J(a.GenderOrder, [])
  };
}
const nn = new Set(Object.keys(P));
function an(t) {
  if (!t || typeof t != "object") return {};
  const e = {};
  for (const [a, o] of Object.entries(t))
    nn.has(a) || (e[a] = o);
  return e;
}
function rn(t) {
  if (!t || typeof t != "object") return ve();
  const e = t, a = P;
  return {
    FilenameTemplate: O(e.FilenameTemplate, a.FilenameTemplate),
    FolderTemplate: O(e.FolderTemplate, a.FolderTemplate),
    DateFormat: O(e.DateFormat, a.DateFormat),
    DurationFormat: O(e.DurationFormat, a.DurationFormat),
    Performers: He(e.Performers, a.Performers),
    Tags: He(e.Tags, a.Tags),
    IllegalReplacement: O(e.IllegalReplacement, a.IllegalReplacement),
    SpaceReplacement: O(e.SpaceReplacement, a.SpaceReplacement),
    RemoveCharacters: O(e.RemoveCharacters, a.RemoveCharacters),
    Case: tn(e.Case),
    AsciiTransliterate: _(e.AsciiTransliterate, a.AsciiTransliterate),
    FilenameMax: Fe(e.FilenameMax, a.FilenameMax),
    FullPathMax: Fe(e.FullPathMax, a.FullPathMax),
    DropOrder: J(e.DropOrder, [...a.DropOrder]),
    OnlyOrganized: _(e.OnlyOrganized, a.OnlyOrganized),
    FilenameAsTitle: _(e.FilenameAsTitle, a.FilenameAsTitle),
    RequiredFields: J(e.RequiredFields, [...a.RequiredFields]),
    DuplicateSuffixFormat: O(e.DuplicateSuffixFormat, a.DuplicateSuffixFormat),
    AutoRenamerOnUpdate: _(e.AutoRenamerOnUpdate, a.AutoRenamerOnUpdate),
    StudioDestinations: Vt(e.StudioDestinations),
    TagDestinations: Jt(e.TagDestinations),
    PathDestinations: Yt(e.PathDestinations),
    ExcludeTags: J(e.ExcludeTags, []),
    ExcludeStudioIds: Ht(e.ExcludeStudioIds, []),
    ExcludePaths: Xt(e.ExcludePaths),
    AllowedRoots: J(e.AllowedRoots, []),
    AssociatedExtensions: J(e.AssociatedExtensions, [...a.AssociatedExtensions]),
    DefaultDestination: O(e.DefaultDestination, a.DefaultDestination),
    UnorganizedDestination: O(e.UnorganizedDestination, a.UnorganizedDestination),
    EnableDefaultRelocate: _(e.EnableDefaultRelocate, a.EnableDefaultRelocate),
    EnableStudioDestinations: _(e.EnableStudioDestinations, a.EnableStudioDestinations),
    EnableTagDestinations: _(e.EnableTagDestinations, a.EnableTagDestinations),
    EnableAdvancedRouting: _(e.EnableAdvancedRouting, a.EnableAdvancedRouting),
    RemoveEmptyFolder: _(e.RemoveEmptyFolder, a.RemoveEmptyFolder),
    SqueezeStudioNames: _(e.SqueezeStudioNames, a.SqueezeStudioNames),
    FieldReplacers: Zt(e.FieldReplacers),
    StripLeadingArticles: _(e.StripLeadingArticles, a.StripLeadingArticles),
    Articles: J(e.Articles, [...a.Articles]),
    PreventTitlePerformer: _(e.PreventTitlePerformer, a.PreventTitlePerformer),
    PreventConsecutiveSegments: _(e.PreventConsecutiveSegments, a.PreventConsecutiveSegments)
  };
}
function on(t) {
  if (t.length === 0) return { valid: !0 };
  try {
    return new RegExp(t), { valid: !0 };
  } catch (e) {
    return { valid: !1, message: e instanceof Error ? e.message : String(e) };
  }
}
function ln(t) {
  const e = t.trim();
  return e.length === 0 ? !0 : /^[A-Za-z]:[\\/]/.test(e) || /^[\\/]/.test(e);
}
const sn = /* @__PURE__ */ new Set([
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
function cn(t) {
  return t.length === 0 ? null : /^[a-z0-9]+$/.test(t) ? sn.has(t) ? "This looks like a primary media extension, not a sidecar." : null : "Extensions are letters and numbers only, like srt or nfo.";
}
function dn(t, e) {
  const a = t.trim().toLowerCase();
  return a.length === 0 ? [...e] : e.filter((o) => o.name.toLowerCase().includes(a));
}
function un(t, e, a) {
  if (e.length === 0) return [...t];
  const o = new Set(e);
  return t.filter((l) => !o.has(a(l)));
}
function mn(t, e) {
  const a = new Set(e);
  return t.filter((o) => !a.has(o.value));
}
function kt(t, e) {
  const a = e.find((o) => o.id === t);
  return a ? a.name : `#${t} (missing)`;
}
function hn(t, e) {
  return e.some((a) => a.id === t);
}
function Nt(t, e) {
  const a = t.trim(), o = e.find((l) => l.name.toLowerCase() === a.toLowerCase());
  return o ? o.name : a;
}
const le = "w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none", St = "cursor-pointer rounded-lg border px-2 py-1 text-xs", pn = "border-border bg-card text-foreground hover:border-accent/50 hover:text-accent", fn = "border-accent bg-accent/15 text-foreground";
function se(t) {
  return `${St} ${t ? fn : pn}`;
}
const De = "__custom__";
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
function M({
  value: t,
  onChange: e,
  onFocus: a,
  placeholder: o,
  mono: l = !1,
  inputRef: i
}) {
  return /* @__PURE__ */ n(
    "input",
    {
      ref: i,
      type: "text",
      value: t,
      placeholder: o,
      onChange: (c) => {
        e(c.target.value);
      },
      onFocus: a,
      className: l ? `${le} font-mono` : le
    }
  );
}
function Se({
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
      onChange: (o) => {
        e(o.target.value === "" ? 0 : Number(o.target.value));
      },
      className: `themed-number-input ${le}`
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
      onChange: (o) => {
        e(o.target.value);
      },
      className: le,
      children: a.map((o) => /* @__PURE__ */ n("option", { value: o.value, children: o.label }, o.value))
    }
  );
}
function Pe({
  value: t,
  onChange: e,
  options: a,
  customPlaceholder: o
}) {
  const l = a.find((m) => m.value === t), i = l === void 0, c = i ? De : t, h = l ? `${l.value} → ${l.example}` : t;
  return /* @__PURE__ */ s("div", { children: [
    /* @__PURE__ */ s(
      "select",
      {
        value: c,
        onChange: (m) => {
          const p = m.target.value;
          p === De ? i || e("") : e(p);
        },
        className: le,
        children: [
          a.map((m) => /* @__PURE__ */ s("option", { value: m.value, children: [
            m.value,
            " → ",
            m.example
          ] }, m.value)),
          /* @__PURE__ */ n("option", { value: De, children: "Custom…" })
        ]
      }
    ),
    i ? /* @__PURE__ */ n("div", { className: "mt-2", children: /* @__PURE__ */ n(M, { value: t, onChange: e, placeholder: o, mono: !0 }) }) : /* @__PURE__ */ n("span", { className: "mt-1 block font-mono text-xs text-secondary", children: h })
  ] });
}
function Ve({
  value: t,
  onChange: e,
  options: a,
  customPlaceholder: o
}) {
  const l = !a.some((i) => i.value === t);
  return /* @__PURE__ */ s("div", { children: [
    /* @__PURE__ */ s("div", { className: "flex flex-wrap gap-1", children: [
      a.map((i) => {
        const c = i.value === t;
        return /* @__PURE__ */ n(
          "button",
          {
            type: "button",
            onClick: () => {
              e(i.value);
            },
            className: se(c),
            children: i.label
          },
          i.value || "__empty__"
        );
      }),
      /* @__PURE__ */ n(
        "button",
        {
          type: "button",
          onClick: () => {
            l || e("");
          },
          className: se(l),
          children: "Custom"
        }
      )
    ] }),
    l ? /* @__PURE__ */ n("div", { className: "mt-2", children: /* @__PURE__ */ n(M, { value: t, onChange: e, placeholder: o, mono: !0 }) }) : null
  ] });
}
function Je({
  value: t,
  onChange: e,
  stripLabel: a,
  replaceLabel: o,
  stripHelper: l,
  replaceHelper: i,
  inputPlaceholder: c
}) {
  const h = q(null), [m, p] = k(t !== ""), g = q(t);
  G(() => {
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
      /* @__PURE__ */ n("button", { type: "button", onClick: f, className: se(!u), children: a }),
      /* @__PURE__ */ n("button", { type: "button", onClick: b, className: se(u), children: o })
    ] }),
    u ? /* @__PURE__ */ s("div", { className: "mt-2", children: [
      /* @__PURE__ */ n(
        M,
        {
          value: t,
          onChange: e,
          placeholder: c,
          inputRef: h,
          mono: !0
        }
      ),
      i ? /* @__PURE__ */ n("span", { className: "mt-1 block text-xs text-secondary", children: i }) : null
    ] }) : l ? /* @__PURE__ */ n("span", { className: "mt-1 block text-xs text-secondary", children: l }) : null
  ] });
}
function L({
  label: t,
  checked: e,
  onChange: a,
  helper: o
}) {
  return /* @__PURE__ */ s("div", { children: [
    /* @__PURE__ */ s("label", { className: "flex items-center gap-2 text-sm text-secondary", title: o, children: [
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
    o ? /* @__PURE__ */ n("p", { className: "mt-1 text-xs text-secondary", children: o }) : null
  ] });
}
function xe({
  values: t,
  onChange: e,
  placeholder: a,
  ordered: o = !1,
  normalize: l,
  onReject: i,
  onLiveChange: c
}) {
  const h = xt();
  function m(u) {
    const f = (l ? l(u.value) : u.value).trim();
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
          o ? /* @__PURE__ */ s(V, { children: [
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
              children: /* @__PURE__ */ n(pe, { className: "h-3 w-3" })
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
        className: le,
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
function gn({
  options: t,
  values: e,
  onChange: a
}) {
  const o = new Set(t.map((h) => h.value)), l = e.filter((h) => !o.has(h));
  function i(h) {
    const m = e.includes(h), p = t.map((g) => g.value).filter((g) => g === h ? !m : e.includes(g));
    a([...p, ...l]);
  }
  function c(h) {
    a(e.filter((m) => m !== h));
  }
  return /* @__PURE__ */ s("div", { className: "flex flex-wrap gap-1", children: [
    t.map((h) => {
      const m = e.includes(h.value);
      return /* @__PURE__ */ n(
        "button",
        {
          type: "button",
          onClick: () => {
            i(h.value);
          },
          className: se(m),
          children: h.label
        },
        h.value
      );
    }),
    l.map((h) => /* @__PURE__ */ s(
      "button",
      {
        type: "button",
        onClick: () => {
          c(h);
        },
        className: `${se(!0)} inline-flex items-center gap-1`,
        title: "Not a recognized value — click to remove",
        children: [
          h,
          /* @__PURE__ */ n(pe, { className: "h-3 w-3" })
        ]
      },
      `extra:${h}`
    ))
  ] });
}
function bn({
  options: t,
  values: e,
  onChange: a,
  addPrompt: o
}) {
  const l = (m) => {
    var p;
    return ((p = t.find((g) => g.value === m)) == null ? void 0 : p.label) ?? m;
  }, i = mn(t, e);
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
              "aria-label": `Move ${l(m)} up`,
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
              "aria-label": `Move ${l(m)} down`,
              onClick: () => {
                c(p, 1);
              },
              className: "text-muted hover:text-foreground",
              children: "↓"
            }
          ),
          /* @__PURE__ */ n("span", { children: l(m) }),
          /* @__PURE__ */ n(
            "button",
            {
              type: "button",
              "aria-label": `Remove ${l(m)}`,
              onClick: () => {
                h(p);
              },
              className: "text-muted hover:text-foreground",
              children: /* @__PURE__ */ n(pe, { className: "h-3 w-3" })
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
        className: le,
        children: [
          /* @__PURE__ */ n("option", { value: "", children: o }),
          i.map((m) => /* @__PURE__ */ n("option", { value: m.value, children: m.label }, m.value))
        ]
      }
    ) : null
  ] });
}
function Ye({
  tokens: t,
  values: e,
  onAdd: a
}) {
  return /* @__PURE__ */ s("div", { className: "mt-1", children: [
    /* @__PURE__ */ n("span", { className: "mb-1 block text-xs text-muted", children: "Add a token:" }),
    /* @__PURE__ */ n("div", { className: "flex flex-wrap gap-1", children: t.map((o) => {
      const l = e.includes(o);
      return /* @__PURE__ */ n(
        "button",
        {
          type: "button",
          disabled: l,
          onClick: () => {
            a(o);
          },
          className: l ? `${St} border-border bg-card text-muted font-mono` : `${se(!1)} font-mono`,
          children: o
        },
        o
      );
    }) })
  ] });
}
function Oe({
  rows: t,
  onChange: e,
  makeRow: a,
  renderRow: o,
  addLabel: l,
  ordered: i = !1
}) {
  const [c, h] = k(() => t.map((b, x) => x)), m = q(t.length);
  G(() => {
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
    [N[b], N[d]] = [N[d], N[b]], e(N), h((j) => {
      const R = [...j];
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
          /* @__PURE__ */ n("div", { className: "min-w-0 flex-1 space-y-2", children: o(b, x, (d) => {
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
                children: /* @__PURE__ */ n(vt, { className: "h-4 w-4" })
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
                children: /* @__PURE__ */ n(yt, { className: "h-4 w-4" })
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
              children: /* @__PURE__ */ n(pe, { className: "h-4 w-4" })
            }
          )
        ]
      },
      c.length === t.length ? c[x] : x
    )),
    /* @__PURE__ */ n(Y, { onClick: f, children: l })
  ] });
}
function wt({
  map: t,
  onChange: e,
  renderKey: a,
  renderValue: o,
  renderKeyLabel: l,
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
          /* @__PURE__ */ n("span", { className: "min-w-0 flex-1 truncate font-mono text-sm text-foreground", children: l ? l(d) : d }),
          /* @__PURE__ */ n("span", { className: "flex-1", children: o(t[d], (N) => {
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
              children: /* @__PURE__ */ n(pe, { className: "h-4 w-4" })
            }
          )
        ]
      },
      d
    )),
    /* @__PURE__ */ s("div", { className: "flex items-start gap-2 rounded-xl border border-border bg-card p-3", children: [
      /* @__PURE__ */ n("span", { className: "min-w-0 flex-1", children: a(c, h, g) }),
      /* @__PURE__ */ n("span", { className: "flex-1", children: o(m, p) }),
      /* @__PURE__ */ n(Y, { onClick: b, disabled: c.trim().length === 0 || x, children: i })
    ] }),
    x ? /* @__PURE__ */ n(F, { kind: "error", children: "That key already has a value." }) : null
  ] });
}
function Xe({ pattern: t, isRegex: e }) {
  if (!e) return null;
  const a = on(t);
  return a.valid ? null : /* @__PURE__ */ s(F, { kind: "error", children: [
    "Invalid pattern: ",
    a.message
  ] });
}
function ye({ value: t }) {
  return t.trim().length === 0 || ln(t) ? null : /* @__PURE__ */ n(F, { kind: "warning", children: "Doesn't look like an absolute path." });
}
function K({
  title: t,
  description: e,
  headerRight: a,
  children: o
}) {
  return /* @__PURE__ */ s("div", { className: "rounded-xl border border-border bg-card p-4", children: [
    a ? /* @__PURE__ */ s("div", { className: "flex items-center justify-between gap-4", children: [
      /* @__PURE__ */ n("h3", { className: "text-base font-semibold text-foreground", children: t }),
      a
    ] }) : /* @__PURE__ */ n("h3", { className: "text-base font-semibold text-foreground", children: t }),
    e ? /* @__PURE__ */ n("p", { className: "mb-4 mt-1 text-sm text-secondary", children: e }) : /* @__PURE__ */ n("div", { className: "mb-4" }),
    /* @__PURE__ */ n("div", { className: "space-y-4", children: o })
  ] });
}
function H({
  title: t,
  summary: e,
  defaultOpen: a = !1,
  children: o
}) {
  const [l, i] = k(a);
  return /* @__PURE__ */ s("div", { className: "overflow-hidden rounded-xl border border-border", children: [
    /* @__PURE__ */ s(
      "button",
      {
        type: "button",
        onClick: () => {
          i((c) => !c);
        },
        "aria-expanded": l,
        className: "flex w-full items-center justify-between gap-4 bg-card px-4 py-3 text-left transition-colors hover:bg-card-hover",
        children: [
          /* @__PURE__ */ s("span", { className: "min-w-0", children: [
            /* @__PURE__ */ n("span", { className: "block text-sm font-medium text-foreground", children: t }),
            e ? /* @__PURE__ */ n("span", { className: "mt-1 block truncate text-xs text-muted", children: e }) : null
          ] }),
          l ? /* @__PURE__ */ n(vt, { className: "h-4 w-4 shrink-0 text-muted" }) : /* @__PURE__ */ n(yt, { className: "h-4 w-4 shrink-0 text-muted" })
        ]
      }
    ),
    l ? /* @__PURE__ */ n("div", { className: "space-y-4 border-t border-border px-4 py-4", children: o }) : null
  ] });
}
function Le({
  children: t,
  onClick: e,
  disabled: a
}) {
  return /* @__PURE__ */ n(
    "button",
    {
      type: "button",
      onClick: e,
      disabled: a,
      className: "inline-flex items-center gap-2 rounded-lg bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-60",
      children: t
    }
  );
}
function Y({
  children: t,
  onClick: e,
  disabled: a
}) {
  return /* @__PURE__ */ n(
    "button",
    {
      type: "button",
      onClick: e,
      disabled: a,
      className: "inline-flex items-center gap-1.5 rounded-lg border border-border bg-card px-3 py-2 text-sm font-medium text-secondary hover:border-accent/50 hover:bg-card-hover hover:text-foreground disabled:opacity-60",
      children: t
    }
  );
}
function F({ kind: t, children: e }) {
  return /* @__PURE__ */ n("span", { className: `text-xs ${t === "success" ? "text-green-400" : t === "error" ? "text-red-400" : t === "warning" ? "text-amber-400" : "text-secondary"}`, children: e });
}
function Q() {
  return /* @__PURE__ */ n(zt, { className: "h-4 w-4 animate-spin" });
}
const _e = "com.alextomas955.renamer", xn = `/extensions/${_e}/list-studios`, vn = `/extensions/${_e}/list-tags`, yn = `/extensions/${_e}/list-performers`, kn = "w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none", Nn = "cursor-pointer rounded-lg px-2 py-1 text-left text-sm text-foreground hover:bg-card-hover", Ze = "inline-flex items-center gap-1 rounded-lg border border-border bg-card px-2 py-0.5 text-xs text-foreground", Sn = "border-red-400 text-red-400";
function Me({
  label: t,
  helper: e,
  values: a,
  onChange: o,
  endpointPath: l,
  adapter: i,
  placeholder: c,
  excludeValues: h
}) {
  const m = xt(), [p, g] = k(""), [u, f] = k(!1), [b, x] = k([]), [d, N] = k(!1), [j, R] = k(!1), [X, ee] = k(!1), S = q(!1), z = q(null);
  G(() => {
    if (!u) return;
    const C = (W) => {
      var ce;
      (ce = z.current) != null && ce.contains(W.target) || f(!1);
    };
    return document.addEventListener("mousedown", C), () => {
      document.removeEventListener("mousedown", C);
    };
  }, [u]);
  const te = ke(async () => {
    if (!(S.current || d)) {
      S.current = !0, R(!0);
      try {
        const C = await A(l);
        x(C), N(!0), ee(!1);
      } catch {
        ee(!0);
      } finally {
        S.current = !1, R(!1);
      }
    }
  }, [l, d]);
  function ie() {
    f(!0), te();
  }
  function ne(C) {
    const W = i.toValue(C, b);
    a.includes(W) || o([...a, W]), g(""), f(!1);
  }
  function ae(C) {
    o(a.filter((W) => W !== C));
  }
  const re = h ? [...a, ...h] : a, D = un(b, re, i.valueOf), I = dn(p, D);
  return /* @__PURE__ */ s(w, { label: t, helper: e, children: [
    a.length > 0 ? /* @__PURE__ */ n("div", { className: "mb-1 flex flex-wrap gap-1", children: a.map((C) => {
      const W = d && !i.isResolved(C, b);
      return /* @__PURE__ */ s(
        "span",
        {
          className: W ? `${Ze} ${Sn}` : Ze,
          children: [
            /* @__PURE__ */ n("span", { children: i.toLabel(C, b) }),
            /* @__PURE__ */ n(
              "button",
              {
                type: "button",
                "aria-label": `Remove ${i.toLabel(C, b)}`,
                onClick: () => {
                  ae(C);
                },
                className: "text-muted hover:text-foreground",
                children: /* @__PURE__ */ n(pe, { className: "h-3 w-3" })
              }
            )
          ]
        },
        String(C)
      );
    }) }) : null,
    /* @__PURE__ */ s("div", { className: "relative", ref: z, children: [
      /* @__PURE__ */ n(
        "input",
        {
          id: m,
          type: "text",
          value: p,
          placeholder: c,
          className: kn,
          onFocus: ie,
          onChange: (C) => {
            g(C.target.value), f(!0);
          },
          onKeyDown: (C) => {
            C.key === "Enter" ? (C.preventDefault(), p.trim() !== "" && I.length > 0 && ne(I[0])) : C.key === "Escape" && f(!1);
          }
        }
      ),
      u && !X ? /* @__PURE__ */ n("div", { className: "mt-1 flex max-h-48 flex-col gap-0.5 overflow-auto rounded-xl border border-border bg-card p-1", children: j ? /* @__PURE__ */ s("span", { className: "flex items-center gap-2 px-2 py-1 text-sm text-muted", children: [
        /* @__PURE__ */ n(Q, {}),
        "Loading…"
      ] }) : I.length === 0 ? /* @__PURE__ */ n("span", { className: "px-2 py-1 text-sm text-muted", children: "No matches" }) : I.map((C) => /* @__PURE__ */ n(
        "button",
        {
          type: "button",
          className: Nn,
          onClick: () => {
            ne(C);
          },
          children: C.name
        },
        C.id
      )) }) : null
    ] }),
    X ? /* @__PURE__ */ n("span", { className: "mt-1 block", children: /* @__PURE__ */ n(F, { kind: "error", children: "Could not load the list — existing values stay editable." }) }) : null
  ] });
}
const wn = {
  toValue: (t) => t.id,
  valueOf: (t) => t.id,
  toLabel: (t, e) => kt(t, e),
  isResolved: (t, e) => hn(t, e)
}, Tn = {
  // A picked row already carries the canonical spelling; canonicalTagName also folds a typed casing.
  toValue: (t, e) => Nt(t.name, e),
  valueOf: (t) => t.name,
  toLabel: (t) => t,
  // A tag value is the name itself, so it is always displayable; "resolved" tracks list membership.
  isResolved: (t, e) => e.some((a) => a.name.toLowerCase() === t.toLowerCase())
}, Cn = {
  toValue: (t, e) => Nt(t.name, e),
  valueOf: (t) => t.name,
  toLabel: (t) => t,
  isResolved: (t, e) => e.some((a) => a.name.toLowerCase() === t.toLowerCase())
};
function Tt({
  label: t,
  helper: e,
  values: a,
  onChange: o,
  placeholder: l,
  excludeValues: i
}) {
  return /* @__PURE__ */ n(
    Me,
    {
      label: t,
      helper: e,
      values: a,
      onChange: o,
      endpointPath: xn,
      adapter: wn,
      placeholder: l,
      excludeValues: i
    }
  );
}
function we({
  label: t,
  helper: e,
  values: a,
  onChange: o,
  placeholder: l,
  excludeValues: i
}) {
  return /* @__PURE__ */ n(
    Me,
    {
      label: t,
      helper: e,
      values: a,
      onChange: o,
      endpointPath: vn,
      adapter: Tn,
      placeholder: l,
      excludeValues: i
    }
  );
}
function Qe({
  label: t,
  helper: e,
  values: a,
  onChange: o,
  placeholder: l,
  excludeValues: i
}) {
  return /* @__PURE__ */ n(
    Me,
    {
      label: t,
      helper: e,
      values: a,
      onChange: o,
      endpointPath: yn,
      adapter: Cn,
      placeholder: l,
      excludeValues: i
    }
  );
}
function En(t) {
  const e = {};
  for (const [a, o] of Object.entries(t)) e[a] = o;
  return e;
}
function Rn(t) {
  const e = {};
  for (const [a, o] of Object.entries(t)) {
    const l = Number(a);
    Number.isInteger(l) && typeof o == "string" && (e[l] = o);
  }
  return e;
}
const $n = "com.alextomas955.renamer", An = `/extensions/${$n}/list-studios`;
function Dn({
  map: t,
  onChange: e
}) {
  const [a, o] = k([]);
  return G(() => {
    let l = !0;
    return A(An).then((i) => {
      l && o(i);
    }).catch(() => {
    }), () => {
      l = !1;
    };
  }, []), /* @__PURE__ */ n(
    wt,
    {
      map: En(t),
      onChange: (l) => {
        e(Rn(l));
      },
      renderKey: (l, i, c) => /* @__PURE__ */ n(Pn, { draftKey: l, setDraftKey: i, existingKeys: c }),
      renderValue: (l, i) => /* @__PURE__ */ s(V, { children: [
        /* @__PURE__ */ n(M, { value: l, onChange: i, placeholder: "Destination root" }),
        /* @__PURE__ */ n(ye, { value: l })
      ] }),
      renderKeyLabel: (l) => kt(Number(l), a),
      addLabel: "Add studio rule"
    }
  );
}
function Pn({
  draftKey: t,
  setDraftKey: e,
  existingKeys: a
}) {
  const o = t === "" ? [] : [Number(t)], l = a.map(Number);
  return /* @__PURE__ */ n(
    Tt,
    {
      label: "Studio",
      values: o,
      onChange: (i) => {
        const c = i.at(-1);
        e(c === void 0 ? "" : String(c));
      },
      placeholder: "Search studios…",
      excludeValues: l
    }
  );
}
const Ue = [
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
function On(t) {
  return `Inserts wrapped in an optional group: ${t.insert} — disappears cleanly when empty.`;
}
function Fn({ onInsert: t }) {
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
    /* @__PURE__ */ n("div", { className: "flex flex-wrap gap-1", children: Ue.map((e) => /* @__PURE__ */ s(
      "button",
      {
        type: "button",
        title: e.kind === "optional" ? On(e) : e.label,
        onClick: () => {
          t(e.insert);
        },
        className: "cursor-pointer rounded-lg border border-border bg-card px-2 py-1 font-mono text-xs text-foreground hover:border-accent/50 hover:text-accent",
        children: [
          e.token,
          e.kind === "optional" ? /* @__PURE__ */ n("span", { className: "ml-1 text-muted", children: "{ }" }) : null
        ]
      },
      e.token
    )) })
  ] });
}
function In(t, e) {
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
function Ln({ result: t }) {
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
      const a = In(e, t);
      return a ? /* @__PURE__ */ n("p", { className: "text-xs text-amber-400", children: a }, e) : null;
    }) }) : null
  ] });
}
const et = 'a[href], button:not([disabled]), textarea, input, select, [tabindex]:not([tabindex="-1"])';
function Ct({
  titleId: t,
  describedById: e,
  pending: a = !1,
  onCancel: o,
  size: l = "lg",
  children: i
}) {
  const c = q(null), h = ke(() => {
    a || o();
  }, [a, o]);
  return G(() => {
    const p = document.activeElement, g = c.current, u = g == null ? void 0 : g.querySelector(et);
    return u == null || u.focus(), () => p == null ? void 0 : p.focus();
  }, []), G(() => {
    function p(g) {
      if (g.key === "Escape") {
        g.preventDefault(), h();
        return;
      }
      if (g.key !== "Tab") return;
      const u = c.current;
      if (!u) return;
      const f = Array.from(u.querySelectorAll(et));
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
        className: `relative ${l === "sm" ? "max-w-sm" : l === "xl" ? "max-w-5xl" : "max-w-2xl"} w-full mx-4 rounded-lg border border-border bg-surface p-6 shadow-xl`,
        children: i
      }
    )
  ] });
}
function _n({ children: t }) {
  return /* @__PURE__ */ n("div", { className: "rounded border border-red-700 bg-red-950/60 px-3 py-2 text-sm text-red-200", children: t });
}
const Et = "com.alextomas955.renamer", Mn = `/extensions/${Et}/last-batch`, Un = `/extensions/${Et}/undo`, tt = "rename-undo-confirm-title", nt = "rename-undo-confirm-message", Bn = 621355968e5, Rt = 1e4, jn = Bn * Rt;
function zn(t) {
  return (t - jn) / Rt;
}
function Kn(t, e = Date.now()) {
  const a = e - t, o = Math.round(a / 1e3);
  if (o < 45) return "just now";
  const l = Math.round(o / 60);
  if (l < 60) return `${l} minute${l === 1 ? "" : "s"} ago`;
  const i = Math.round(l / 60);
  if (i < 24) return `${i} hour${i === 1 ? "" : "s"} ago`;
  const c = Math.round(i / 24);
  return c === 1 ? "yesterday" : c <= 7 ? `${c} days ago` : new Date(t).toLocaleDateString();
}
function at(t) {
  return t instanceof Z ? `${t.status} ${t.body}` : String(t);
}
function qn({ refreshKey: t }) {
  const [e, a] = k(null), [o, l] = k(!0), [i, c] = k(null), [h, m] = k(!1), [p, g] = k(!1), [u, f] = k(null), b = ke(async () => {
    l(!0), c(null);
    try {
      const R = await A(Mn);
      a(R);
    } catch (R) {
      c(at(R));
    } finally {
      l(!1);
    }
  }, []);
  G(() => {
    b();
  }, [b, t]);
  const x = !!e && e.hasBatch && !e.consumed, d = (e == null ? void 0 : e.count) ?? 0, N = e ? zn(e.writtenAtUtcTicks) : 0;
  async function j() {
    var R, X, ee, S, z, te, ie, ne, ae, re;
    g(!0), f(null);
    try {
      const D = await A(Un, { method: "POST" }), I = (((R = D.failed) == null ? void 0 : R.length) ?? 0) + (((X = D.skipped) == null ? void 0 : X.length) ?? 0);
      if (I === 0)
        f({
          kind: "success",
          text: `Undone — ${D.undone} file${D.undone === 1 ? "" : "s"} moved back to their original names.`
        });
      else if (D.undone > 0) {
        const C = ((S = (ee = D.failed) == null ? void 0 : ee[0]) == null ? void 0 : S.reason) ?? ((te = (z = D.skipped) == null ? void 0 : z[0]) == null ? void 0 : te.reason) ?? "unknown reason";
        f({
          kind: "error",
          text: `Undo finished with problems — ${I} file${I === 1 ? "" : "s"} couldn't be moved back (${C}). The rest were restored.`
        });
      } else {
        const C = ((ne = (ie = D.failed) == null ? void 0 : ie[0]) == null ? void 0 : ne.reason) ?? ((re = (ae = D.skipped) == null ? void 0 : ae[0]) == null ? void 0 : re.reason) ?? "unknown reason";
        f({ kind: "error", text: `Couldn't undo — ${C}. Nothing was changed.` });
      }
    } catch (D) {
      if (D instanceof Z) {
        f({
          kind: "error",
          text: `Couldn't undo — ${at(D)}. Nothing was changed.`
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
    o ? /* @__PURE__ */ s("div", { className: "flex items-center gap-2 text-sm text-secondary", children: [
      /* @__PURE__ */ n(Q, {}),
      "Checking for a recent rename…"
    ] }) : i ? /* @__PURE__ */ s("div", { className: "space-y-2", children: [
      /* @__PURE__ */ s(F, { kind: "error", children: [
        "Couldn't check for a recent rename — ",
        i,
        "."
      ] }),
      /* @__PURE__ */ n("div", { children: /* @__PURE__ */ n(Y, { onClick: () => void b(), children: "Retry" }) })
    ] }) : x ? /* @__PURE__ */ s("div", { className: "space-y-3", children: [
      /* @__PURE__ */ s("div", { className: "flex items-center justify-between gap-3", children: [
        /* @__PURE__ */ s("span", { className: "text-sm text-foreground", children: [
          "Last rename: ",
          d,
          " item",
          d === 1 ? "" : "s",
          " renamed · ",
          Kn(N)
        ] }),
        /* @__PURE__ */ s(
          Y,
          {
            onClick: () => {
              m(!0);
            },
            disabled: p,
            children: [
              /* @__PURE__ */ n(Kt, { className: "h-4 w-4" }),
              "Undo last rename"
            ]
          }
        )
      ] }),
      u ? /* @__PURE__ */ n(F, { kind: u.kind, children: u.text }) : null
    ] }) : /* @__PURE__ */ s("div", { className: "space-y-2", children: [
      /* @__PURE__ */ n("span", { className: "text-sm text-secondary", children: "No rename to undo." }),
      u ? /* @__PURE__ */ n("div", { children: /* @__PURE__ */ n(F, { kind: u.kind, children: u.text }) }) : null
    ] }),
    h ? /* @__PURE__ */ s(
      Ct,
      {
        titleId: tt,
        describedById: nt,
        pending: p,
        onCancel: () => {
          m(!1);
        },
        size: "sm",
        children: [
          /* @__PURE__ */ n("h2", { id: tt, className: "mb-2 text-lg font-semibold text-foreground", children: "Undo last rename?" }),
          /* @__PURE__ */ s("p", { id: nt, className: "mb-6 text-sm text-secondary", children: [
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
                onClick: () => void j(),
                disabled: p,
                className: "inline-flex items-center gap-2 rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-500 disabled:opacity-60",
                children: [
                  p ? /* @__PURE__ */ n(Q, {}) : null,
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
const Gn = {
  amber: "border-amber-400/40 bg-amber-400/10 text-amber-400",
  gray: "border-border bg-card text-muted",
  red: "border-red-700/50 bg-red-950/40 text-red-400"
};
function Wn(t) {
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
function Hn({ badge: t }) {
  const e = t.variant === "amber" || t.variant === "red";
  return /* @__PURE__ */ s(
    "span",
    {
      className: `inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs font-medium ${Gn[t.variant]}`,
      children: [
        e ? /* @__PURE__ */ n(Ee, { className: "h-3 w-3" }) : null,
        t.label
      ]
    }
  );
}
function Vn({ item: t }) {
  const e = Wn(t);
  return e.length === 0 ? null : /* @__PURE__ */ n("span", { className: "inline-flex flex-wrap gap-1", children: e.map((a) => /* @__PURE__ */ n(Hn, { badge: a }, a.label)) });
}
const Jn = /* @__PURE__ */ new Set([
  "SkipGated",
  "SkipCollision",
  "SkipLocked",
  "SkipBlocked",
  "SkipNoSpace",
  "SkipExcluded",
  "Failed"
]);
function $t(t) {
  let e = 0, a = 0;
  for (const o of t)
    o.status === "Renamer" || o.status === "Move" ? e++ : Jn.has(o.status) && a++;
  return { renamed: e, skipped: a, scanned: t.length };
}
function Yn(t, e, a = 50) {
  return t.slice(e * a, e * a + a);
}
function Xn(t, e = 50) {
  return Math.max(1, Math.ceil(t / e));
}
const At = "com.alextomas955.renamer", Zn = `/extensions/${At}/scan-library`, Qn = `/extensions/${At}/last-scan`, rt = "rename-dry-run-title", ot = "rename-dry-run-summary", lt = 50, ea = 1e3;
function st(t) {
  return t instanceof Z ? `${t.status} ${t.body}` : String(t);
}
function it(t) {
  if (!t) return t;
  const e = Math.max(t.lastIndexOf("/"), t.lastIndexOf("\\"));
  return e >= 0 ? t.slice(e + 1) : t;
}
function ta(t, e) {
  G(() => {
    if (!t) return;
    let a = !1;
    const o = setInterval(() => {
      A(`/jobs/${t}`).then((l) => {
        a || (l.status === "completed" || l.status === "failed" || l.status === "cancelled") && (clearInterval(o), e(l));
      }).catch(() => {
      });
    }, ea);
    return () => {
      a = !0, clearInterval(o);
    };
  }, [t]);
}
function na({
  onClose: t,
  onRenameAll: e,
  renaming: a
}) {
  const [o, l] = k(null), [i, c] = k(null), [h, m] = k(null), [p, g] = k(0), u = q(!1);
  G(() => {
    u.current || (u.current = !0, A(Zn, { method: "POST" }).then((d) => {
      l(d.jobId);
    }).catch((d) => {
      m(st(d));
    }));
  }, []), ta(o, (d) => {
    if (d.status !== "completed") {
      m(d.error ?? "the scan job did not complete");
      return;
    }
    A(Qn).then((N) => {
      c(N);
    }).catch((N) => {
      m(st(N));
    });
  });
  const f = i ? $t(i) : null, b = i ? Xn(i.length, lt) : 1, x = i ? Yn(i, p, lt) : [];
  return /* @__PURE__ */ s(
    Ct,
    {
      titleId: rt,
      describedById: ot,
      pending: a,
      onCancel: t,
      size: "xl",
      children: [
        /* @__PURE__ */ n("h2", { id: rt, className: "mb-2 text-lg font-semibold text-foreground", children: "Dry run" }),
        h ? /* @__PURE__ */ n("div", { className: "mb-4", children: /* @__PURE__ */ s(_n, { children: [
          "Couldn't scan your library — ",
          h,
          ". Close and try again."
        ] }) }) : i === null || f === null ? /* @__PURE__ */ s("div", { className: "flex items-center gap-2 py-8 text-sm text-secondary", children: [
          /* @__PURE__ */ n(Q, {}),
          "Scanning your library…"
        ] }) : /* @__PURE__ */ s(V, { children: [
          /* @__PURE__ */ s("p", { id: ot, className: "mb-4 text-sm text-secondary", children: [
            /* @__PURE__ */ n("span", { className: "text-foreground", children: f.renamed }),
            " will be renamed ·",
            " ",
            f.skipped,
            " skipped · ",
            f.scanned,
            " scanned"
          ] }),
          f.scanned === 0 ? /* @__PURE__ */ n("p", { className: "py-8 text-center text-sm text-secondary", children: "No items match your current settings — nothing to rename." }) : /* @__PURE__ */ n(V, { children: /* @__PURE__ */ s("div", { className: "max-h-96 overflow-y-auto rounded border border-border text-sm", children: [
            /* @__PURE__ */ s("table", { className: "w-full border-collapse", children: [
              /* @__PURE__ */ n("thead", { children: /* @__PURE__ */ s("tr", { className: "sticky top-0 bg-card text-left", children: [
                /* @__PURE__ */ n("th", { className: "w-20 px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted", children: "Type" }),
                /* @__PURE__ */ n("th", { className: "min-w-0 flex-1 px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted", children: "Current name" }),
                /* @__PURE__ */ n("th", { className: "min-w-0 flex-1 px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted", children: "New name" }),
                /* @__PURE__ */ n("th", { className: "min-w-0 flex-1 px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted", children: "Destination" }),
                /* @__PURE__ */ n("th", { className: "px-3 py-2" })
              ] }) }),
              /* @__PURE__ */ n("tbody", { className: "divide-y divide-border", children: x.map((d) => {
                const N = d.status !== "Renamer" && d.status !== "Move", j = it(d.oldFullPath), R = d.newBasename || it(d.newFullPath);
                return /* @__PURE__ */ s("tr", { className: N ? "opacity-70" : void 0, children: [
                  /* @__PURE__ */ n("td", { className: "w-20 px-3 py-2 text-sm text-secondary", children: d.kind }),
                  /* @__PURE__ */ n(
                    "td",
                    {
                      className: "min-w-0 max-w-0 truncate px-3 py-2 font-mono text-sm text-muted",
                      title: d.oldFullPath,
                      children: j
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
                  /* @__PURE__ */ n("td", { className: "px-3 py-2", children: /* @__PURE__ */ n(Vn, { item: d }) })
                ] }, d.fileId);
              }) })
            ] }),
            /* @__PURE__ */ s("div", { className: "flex items-center justify-between border-t border-border bg-card px-3 py-2", children: [
              /* @__PURE__ */ n(
                Y,
                {
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
                Y,
                {
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
          /* @__PURE__ */ n(Y, { onClick: t, disabled: a, children: "Close" }),
          /* @__PURE__ */ s(
            Le,
            {
              onClick: () => {
                i && e(i);
              },
              disabled: a || !f || f.renamed === 0,
              children: [
                a ? /* @__PURE__ */ n(Q, {}) : null,
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
const Be = new Set(Ue.map((t) => t.token.slice(1).toLowerCase())), ct = Ue.map((t) => t.token.slice(1));
function aa(t) {
  const e = t.startsWith("$") ? t.slice(1) : t;
  return Be.has(e.toLowerCase());
}
function ra(t) {
  let e = 0;
  for (const a of t)
    if (a === "{") e++;
    else if (a === "}" && (e--, e < 0))
      return !1;
  return e === 0;
}
function oa(t) {
  const e = [], a = /* @__PURE__ */ new Set();
  for (let o = 0; o < t.length; o++) {
    if (t[o] !== "$") continue;
    if (t[o + 1] === "$") {
      o++;
      continue;
    }
    let l = o + 1;
    for (; l < t.length && /[A-Za-z0-9_]/.test(t[l]); ) l++;
    if (l === o + 1) continue;
    const i = t.slice(o + 1, l), c = i.toLowerCase();
    !Be.has(c) && !a.has(c) && (a.add(c), e.push(`$${i}`)), o = l - 1;
  }
  return e;
}
function dt(t, e) {
  for (let a = 0; a < t.length; a++) {
    if (t[a] !== "$") continue;
    if (t[a + 1] === "$") {
      a++;
      continue;
    }
    let o = a + 1;
    for (; o < t.length && /[A-Za-z0-9_]/.test(t[o]); ) o++;
    if (o !== a + 1) {
      if (t.slice(a + 1, o).toLowerCase() === e) return !0;
      a = o - 1;
    }
  }
  return !1;
}
function Te(t, e, a) {
  const o = (t.startsWith("$") ? t.slice(1) : t).toLowerCase();
  return dt(e, o) || dt(a, o);
}
function la(t, e) {
  const a = t.length, o = e.length, l = Array.from({ length: o + 1 }, (i, c) => c);
  for (let i = 1; i <= a; i++) {
    let c = l[0];
    l[0] = i;
    for (let h = 1; h <= o; h++) {
      const m = l[h];
      l[h] = t[i - 1] === e[h - 1] ? c : Math.min(c, l[h - 1], l[h]) + 1, c = m;
    }
  }
  return l[o];
}
function Dt(t) {
  const e = (t.startsWith("$") ? t.slice(1) : t).toLowerCase();
  let a, o = 1 / 0;
  for (const l of Be) {
    const i = la(e, l);
    i < o && (o = i, a = l);
  }
  return a !== void 0 && o > 0 && o <= 2 ? `$${a}` : void 0;
}
const sa = [
  // The shipped default, offered as a chip so a user who edits the template can return to it in one
  // click. The string matches DEFAULT_OPTIONS.FilenameTemplate exactly so the chip and "Reset to
  // defaults" produce the identical template.
  { label: "Date – Title [Height]", filenameTemplate: "{$date - }$title{ [$height]}" },
  { label: "Title + resolution", filenameTemplate: "$title{ [$resolution]}" },
  { label: "Studio – Title [Res]", filenameTemplate: "$studio{ - $title}{ [$resolution]}" },
  { label: "Date – Title", filenameTemplate: "$date{ - $title}" },
  { label: "Performers – Title", filenameTemplate: "$performers{ - $title}" }
], he = "com.alextomas955.renamer", Pt = "options", ia = `/extensions/${he}/data`, ca = `/extensions/${he}/preview-sample`, da = `/extensions/${he}/rename-library`, ua = 250, ma = 1e3;
function ut(t) {
  let e = t.trim();
  return e.startsWith(".") && (e = e.slice(1)), e.toLowerCase();
}
const ha = [
  { value: "None", label: "None" },
  { value: "Lower", label: "lower case" },
  { value: "Title", label: "Title Case" }
], mt = [
  { value: "DropAll", label: "Drop all when over the max" },
  { value: "KeepFirst", label: "Keep the first N" }
], pa = [
  { value: "NameAsc", label: "Name (A→Z)" },
  { value: "None", label: "Keep original order" },
  { value: "IdAsc", label: "By internal id" },
  { value: "FavoriteFirst", label: "Favorites first, then name" }
], fa = [
  { value: "NameAsc", label: "Name (A→Z)" },
  { value: "None", label: "Keep original order" }
], ht = [
  { value: "Male", label: "Male" },
  { value: "Female", label: "Female" },
  { value: "TransgenderMale", label: "Transgender male" },
  { value: "TransgenderFemale", label: "Transgender female" },
  { value: "Intersex", label: "Intersex" },
  { value: "NonBinary", label: "Non-binary" }
], Ce = [
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
].map((t) => ({ value: t, label: t })), ga = [
  { value: "yyyy-MM-dd", example: "2026-03-12" },
  { value: "yyyy", example: "2026" },
  { value: "MM-dd-yyyy", example: "03-12-2026" },
  { value: "dd.MM.yyyy", example: "12.03.2026" },
  { value: "yyyy.MM.dd", example: "2026.03.12" }
], ba = [
  { value: "hh\\-mm\\-ss", example: "01-23-45" },
  { value: "hh\\.mm\\.ss", example: "01.23.45" },
  { value: "mm\\-ss", example: "83-45" }
], pt = [
  { value: ", ", label: "Comma + space ( , )" },
  { value: " · ", label: "Middot ( · )" },
  { value: " ", label: "Space ( ␣ )" },
  { value: " - ", label: "Dash ( - )" }
], xa = [
  { value: " ({n})", example: "name (1).mp4" },
  { value: "_{n}", example: "name_1.mp4" },
  { value: " - {n}", example: "name - 1.mp4" }
];
function ft({
  value: t,
  emptySamples: e = []
}) {
  const a = [];
  ra(t) || a.push("Unmatched { or } — it'll still render, but check your groups.");
  for (const o of oa(t)) {
    const l = Dt(o);
    a.push(
      l ? `${o} isn't a known token — it'll render as empty. Did you mean ${l}?` : `${o} isn't a known token — it'll render as empty.`
    );
  }
  for (const o of e)
    a.push(`This template produces an empty name for the "${o}" sample.`);
  return a.length === 0 ? null : /* @__PURE__ */ n("div", { className: "mt-1 space-y-1", role: "status", "aria-live": "polite", children: a.map((o) => /* @__PURE__ */ s("p", { className: "flex items-start gap-1 text-xs text-amber-400", children: [
    /* @__PURE__ */ n(Ee, { className: "h-3 w-3 shrink-0" }),
    /* @__PURE__ */ n("span", { children: o })
  ] }, o)) });
}
function gt({ values: t }) {
  const e = [];
  for (const a of t) {
    if (aa(a)) continue;
    const o = Dt(a), l = o ? o.slice(1) : void 0;
    e.push(
      l ? `"${a}" isn't a known token — it'll be ignored. Did you mean ${l}?` : `"${a}" isn't a known token — it'll be ignored.`
    );
  }
  return e.length === 0 ? null : /* @__PURE__ */ n("div", { className: "mt-1 space-y-1", role: "status", "aria-live": "polite", children: e.map((a) => /* @__PURE__ */ s("p", { className: "flex items-start gap-1 text-xs text-amber-400", children: [
    /* @__PURE__ */ n(Ee, { className: "h-3 w-3 shrink-0" }),
    /* @__PURE__ */ n("span", { children: a })
  ] }, a)) });
}
function va({ onApply: t }) {
  return /* @__PURE__ */ s("div", { children: [
    /* @__PURE__ */ n("span", { className: "mb-1 block text-xs font-medium uppercase tracking-wide text-muted", children: "Presets" }),
    /* @__PURE__ */ n("div", { className: "flex flex-wrap gap-1", children: sa.map((e) => /* @__PURE__ */ n(
      "button",
      {
        type: "button",
        title: e.filenameTemplate,
        onClick: () => {
          t(e.filenameTemplate);
        },
        className: "cursor-pointer rounded-lg border border-border bg-card px-2 py-1 text-xs text-foreground hover:border-accent/50 hover:text-accent",
        children: e.label
      },
      e.label
    )) }),
    /* @__PURE__ */ n("p", { className: "mt-1 text-xs text-muted", children: "Click a preset to fill the filename template. You can edit it afterwards." })
  ] });
}
function me({ title: t, children: e }) {
  return /* @__PURE__ */ s("div", { className: "rounded-2xl border border-border bg-surface p-5", children: [
    /* @__PURE__ */ n("h2", { className: "border-b border-border pb-3 mb-4 text-base font-semibold text-foreground", children: t }),
    /* @__PURE__ */ n("div", { className: "space-y-4", children: e })
  ] });
}
function ya({
  dirty: t,
  saving: e,
  saveError: a,
  savedFlash: o,
  canSave: l,
  onSave: i,
  onDiscard: c
}) {
  return t ? /* @__PURE__ */ n("div", { className: "fixed inset-x-0 bottom-0 z-50 border-t border-border bg-surface px-6 py-4", children: /* @__PURE__ */ s("div", { className: "flex items-center gap-3", children: [
    a ? /* @__PURE__ */ s(F, { kind: "error", children: [
      "Couldn't save settings — ",
      a,
      ". Your changes are still here; try Save again."
    ] }) : o ? /* @__PURE__ */ n(F, { kind: "success", children: "Settings saved." }) : /* @__PURE__ */ n(F, { kind: "muted", children: "Unsaved changes" }),
    /* @__PURE__ */ s("div", { className: "ml-auto flex items-center gap-3", children: [
      /* @__PURE__ */ n(Y, { onClick: c, disabled: e, children: "Discard" }),
      /* @__PURE__ */ s(Le, { onClick: i, disabled: !l || e, children: [
        e ? /* @__PURE__ */ n(Q, {}) : null,
        "Save changes"
      ] })
    ] })
  ] }) }) : null;
}
async function ka(t, e) {
  const a = { ...e, ...t };
  try {
    await A(`${ia}/${Pt}`, {
      method: "PUT",
      // Double-encode: inner serialize = the stored value; outer serialize makes it a JSON
      // string literal for the [FromBody] string binder.
      body: JSON.stringify(JSON.stringify(a))
    });
  } catch (o) {
    if (o instanceof Z) throw o;
  }
}
function Ot() {
  const t = Wt(he), [e, a] = k(() => ve()), [o, l] = k(() => ve()), [i, c] = k(!0), [h, m] = k(null), [p, g] = k(!1), [u, f] = k(null), [b, x] = k(!1), [d, N] = k(!1), [j, R] = k(!1), [X, ee] = k(""), S = q({}), [z, te] = k(null), [ie, ne] = k(!1), ae = q(null), re = q(null), D = q("filename"), I = JSON.stringify(e) !== JSON.stringify(o), C = I || j, [W, ce] = k(!1), [Re, je] = k(!1), [fe, $e] = k(null);
  function ze(r) {
    return new Promise((E, y) => {
      const T = setInterval(() => {
        A(`/jobs/${r}`).then(($) => {
          $.status === "completed" ? (clearInterval(T), E()) : ($.status === "failed" || $.status === "cancelled") && (clearInterval(T), y(new Error($.error ?? "the job did not complete")));
        }).catch(() => {
        });
      }, ma);
    });
  }
  const Ke = ke(async (r) => {
    je(!0), $e(null);
    try {
      let E = r;
      if (!E) {
        const { jobId: $ } = await A(
          `/extensions/${he}/scan-library`,
          { method: "POST" }
        );
        await ze($), E = await A(`/extensions/${he}/last-scan`);
      }
      const y = $t(E), { jobId: T } = await A(da, { method: "POST" });
      await ze(T), ce(!1), $e({
        kind: "success",
        text: `Renamed ${y.renamed} file${y.renamed === 1 ? "" : "s"}` + (y.skipped > 0 ? `, ${y.skipped} skipped` : "") + "."
      });
    } catch (E) {
      const y = E instanceof Z ? `${E.status} ${E.body}` : String(E);
      $e({
        kind: "error",
        text: `Couldn't rename — ${y}. Nothing was changed; you can try again.`
      });
    } finally {
      je(!1);
    }
  }, []), Ae = ke(async () => {
    c(!0), m(null), R(!1);
    try {
      const E = (await t.getAll())[Pt];
      if (E) {
        N(!1);
        let y;
        try {
          y = JSON.parse(E);
        } catch {
          S.current = {};
          const de = ve();
          a(de), l(de), R(!0);
          return;
        }
        S.current = an(y);
        const T = rn(y), $ = {
          ...T,
          EnableStudioDestinations: T.EnableStudioDestinations || Object.keys(T.StudioDestinations).length > 0,
          EnableTagDestinations: T.EnableTagDestinations || Object.keys(T.TagDestinations).length > 0,
          EnableAdvancedRouting: T.EnableAdvancedRouting || T.AllowedRoots.length > 0 || T.PathDestinations.length > 0
        };
        a($), l($);
      } else {
        N(!0), S.current = {};
        const y = ve();
        a(y), l(y);
      }
    } catch (r) {
      m(r instanceof Z ? `${r.status} ${r.body}` : String(r));
    } finally {
      c(!1);
    }
  }, [t]);
  G(() => {
    Ae();
  }, [Ae]), G(() => {
    if (i) return;
    const r = setTimeout(() => {
      A(ca, {
        method: "POST",
        body: JSON.stringify({ Options: e })
      }).then((E) => {
        te(E), ne(!1);
      }).catch(() => {
        ne(!0);
      });
    }, ua);
    return () => {
      clearTimeout(r);
    };
  }, [e, i]);
  async function It() {
    g(!0), f(null);
    try {
      await ka(e, S.current), l(e), N(!1), R(!1), x(!0), setTimeout(() => {
        x(!1);
      }, 3e3);
    } catch (r) {
      f(r instanceof Z ? `${r.status} ${r.body}` : String(r));
    } finally {
      g(!1);
    }
  }
  function v(r, E) {
    a((y) => ({ ...y, [r]: E }));
  }
  function U(r, E) {
    a((y) => ({ ...y, [r]: { ...y[r], ...E } }));
  }
  function ge(r) {
    const E = D.current, y = E === "folder" ? re.current : ae.current, T = E === "folder" ? "FolderTemplate" : "FilenameTemplate", $ = e[T];
    if (y && typeof y.selectionStart == "number") {
      const de = y.selectionStart, _t = y.selectionEnd ?? de, Mt = $.slice(0, de) + r + $.slice(_t);
      v(T, Mt), requestAnimationFrame(() => {
        y.focus();
        const We = de + r.length;
        y.setSelectionRange(We, We);
      });
    } else
      v(T, $ + r);
  }
  if (i)
    return /* @__PURE__ */ s("div", { className: "flex items-center gap-2 text-sm text-secondary", children: [
      /* @__PURE__ */ n(Q, {}),
      "Loading settings…"
    ] });
  if (h)
    return /* @__PURE__ */ s("div", { className: "space-y-3", children: [
      /* @__PURE__ */ s(F, { kind: "error", children: [
        "Couldn't load your saved settings — ",
        h,
        ". Retry, or continue with defaults below."
      ] }),
      /* @__PURE__ */ n("div", { children: /* @__PURE__ */ n(Y, { onClick: () => void Ae(), children: "Retry" }) })
    ] });
  const B = (r) => e[r], Lt = (z ?? []).filter((r) => r.flags.includes("empty")).map((r) => r.sampleLabel), qe = Te(
    "performers",
    e.FilenameTemplate,
    e.FolderTemplate
  ), Ge = Te("tags", e.FilenameTemplate, e.FolderTemplate), oe = Te("date", e.FilenameTemplate, e.FolderTemplate), be = Te(
    "duration",
    e.FilenameTemplate,
    e.FolderTemplate
  );
  return /* @__PURE__ */ s("div", { className: `space-y-6 ${I ? "pb-20" : ""}`, children: [
    /* @__PURE__ */ s("div", { className: "grid grid-cols-1 gap-6 lg:grid-cols-3", children: [
      /* @__PURE__ */ s("div", { className: "col-span-2", children: [
        j ? /* @__PURE__ */ n(F, { kind: "error", children: "Your saved settings couldn't be read and have been reset to defaults. Review the options below and save to store a clean copy." }) : d ? /* @__PURE__ */ n(F, { kind: "muted", children: "Using default settings — pick a preset or write a template, then save." }) : null,
        /* @__PURE__ */ s(me, { title: "Essentials", children: [
          /* @__PURE__ */ n(
            va,
            {
              onApply: (r) => {
                v("FilenameTemplate", r);
              }
            }
          ),
          /* @__PURE__ */ n(w, { label: "Filename template", children: /* @__PURE__ */ n(
            M,
            {
              value: e.FilenameTemplate,
              onChange: (r) => {
                v("FilenameTemplate", r);
              },
              onFocus: () => D.current = "filename",
              inputRef: ae,
              mono: !0,
              placeholder: "$title"
            }
          ) }),
          /* @__PURE__ */ n(ft, { value: e.FilenameTemplate, emptySamples: Lt }),
          /* @__PURE__ */ n(Fn, { onInsert: ge }),
          /* @__PURE__ */ n(
            w,
            {
              label: "Folder template",
              helper: "Blank = no folder move (rename in place). Use / for sub-folders, e.g. $studio / $year.",
              children: /* @__PURE__ */ n(
                M,
                {
                  value: e.FolderTemplate,
                  onChange: (r) => {
                    v("FolderTemplate", r);
                  },
                  onFocus: () => D.current = "folder",
                  inputRef: re,
                  mono: !0,
                  placeholder: "$studio / $year"
                }
              )
            }
          ),
          /* @__PURE__ */ n(ft, { value: e.FolderTemplate })
        ] })
      ] }),
      /* @__PURE__ */ n("div", { children: /* @__PURE__ */ s("div", { className: "space-y-4 rounded-2xl border border-border bg-surface p-5 lg:sticky lg:top-16", children: [
        /* @__PURE__ */ n("div", { className: "text-base font-semibold text-foreground", children: "Live preview" }),
        /* @__PURE__ */ n("p", { className: "mb-4 mt-1 text-sm text-secondary", children: "Old → new for sample items, before anything touches disk." }),
        ie ? /* @__PURE__ */ n(F, { kind: "error", children: "Preview unavailable — saved naming still works." }) : null,
        z == null ? /* @__PURE__ */ s("div", { className: "flex items-center gap-2 text-sm text-secondary", children: [
          /* @__PURE__ */ n(Q, {}),
          "Rendering preview…"
        ] }) : /* @__PURE__ */ n("div", { className: "space-y-3", children: z.map((r) => /* @__PURE__ */ n(Ln, { result: r }, r.sampleLabel)) })
      ] }) })
    ] }),
    /* @__PURE__ */ s(me, { title: "What Gets Renamed", children: [
      /* @__PURE__ */ n(
        L,
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
        L,
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
              xe,
              {
                values: e.RequiredFields,
                onChange: (r) => {
                  v("RequiredFields", r);
                },
                placeholder: "Add token, press Enter"
              }
            ),
            /* @__PURE__ */ n(
              Ye,
              {
                tokens: ct,
                values: e.RequiredFields,
                onAdd: (r) => {
                  v(
                    "RequiredFields",
                    e.RequiredFields.includes(r) ? e.RequiredFields : [...e.RequiredFields, r]
                  );
                }
              }
            ),
            /* @__PURE__ */ n(gt, { values: e.RequiredFields })
          ]
        }
      )
    ] }),
    /* @__PURE__ */ s(me, { title: "Run & Automation", children: [
      /* @__PURE__ */ n(
        H,
        {
          title: "Automation",
          summary: "Auto-rename when an item's metadata changes",
          children: /* @__PURE__ */ n(
            L,
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
        H,
        {
          title: "Run for the whole library",
          summary: "Preview or rename every matching item in your library",
          children: [
            /* @__PURE__ */ s("div", { className: "flex flex-wrap items-center gap-3", children: [
              /* @__PURE__ */ n(
                Y,
                {
                  onClick: () => {
                    ce(!0);
                  },
                  disabled: I,
                  children: "Dry run"
                }
              ),
              /* @__PURE__ */ s(Le, { onClick: () => void Ke(), disabled: I || Re, children: [
                Re ? /* @__PURE__ */ n(Q, {}) : null,
                "Rename all files"
              ] })
            ] }),
            I ? /* @__PURE__ */ s(
              "p",
              {
                className: "mt-2 flex items-start gap-1 text-xs text-amber-400",
                role: "status",
                "aria-live": "polite",
                children: [
                  /* @__PURE__ */ n(Ee, { className: "h-3 w-3 shrink-0" }),
                  /* @__PURE__ */ n("span", { children: "Save or discard your changes before running this." })
                ]
              }
            ) : null,
            fe ? /* @__PURE__ */ n("p", { className: "mt-2", children: /* @__PURE__ */ s(F, { kind: fe.kind, children: [
              fe.kind === "success" ? "✓ " : "",
              fe.text,
              fe.kind === "success" ? /* @__PURE__ */ s(V, { children: [
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
    W ? /* @__PURE__ */ n(
      na,
      {
        onClose: () => {
          ce(!1);
        },
        onRenameAll: (r) => void Ke(r),
        renaming: Re
      }
    ) : null,
    /* @__PURE__ */ s(me, { title: "Token Settings", children: [
      qe ? /* @__PURE__ */ s(
        H,
        {
          title: "Performers",
          summary: "Separators, limits, sort, and allow/block lists",
          children: [
            /* @__PURE__ */ n(w, { label: "Separator", children: /* @__PURE__ */ n(
              Ve,
              {
                value: B("Performers").Separator,
                onChange: (r) => {
                  U("Performers", { Separator: r });
                },
                options: pt,
                customPlaceholder: "Custom separator"
              }
            ) }),
            /* @__PURE__ */ n(w, { label: "Max count", helper: "0 = unlimited", children: /* @__PURE__ */ n(
              Se,
              {
                value: B("Performers").MaxCount,
                min: 0,
                onChange: (r) => {
                  U("Performers", { MaxCount: r });
                }
              }
            ) }),
            /* @__PURE__ */ n(w, { label: "On overflow", children: /* @__PURE__ */ n(
              ue,
              {
                value: B("Performers").OnOverflow,
                onChange: (r) => {
                  U("Performers", { OnOverflow: r });
                },
                options: mt
              }
            ) }),
            /* @__PURE__ */ n(w, { label: "Sort", helper: "The id and favorite orders apply to performers only.", children: /* @__PURE__ */ n(
              ue,
              {
                value: B("Performers").Sort,
                onChange: (r) => {
                  U("Performers", { Sort: r });
                },
                options: pa
              }
            ) }),
            /* @__PURE__ */ n(
              w,
              {
                label: "Ignore genders",
                helper: "Drop performers of these genders before the max-count limit. A performer with no gender is always kept. None selected = off.",
                children: /* @__PURE__ */ n(
                  gn,
                  {
                    options: ht,
                    values: B("Performers").IgnoreGenders,
                    onChange: (r) => {
                      U("Performers", { IgnoreGenders: r });
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
                  bn,
                  {
                    options: ht,
                    values: B("Performers").GenderOrder,
                    onChange: (r) => {
                      U("Performers", { GenderOrder: r });
                    },
                    addPrompt: "Add a gender…"
                  }
                )
              }
            ),
            /* @__PURE__ */ n(
              Qe,
              {
                label: "Whitelist",
                helper: "If set, only these performers are kept (case-insensitive).",
                values: B("Performers").Whitelist,
                onChange: (r) => {
                  U("Performers", { Whitelist: r });
                },
                placeholder: "Search performers…"
              }
            ),
            /* @__PURE__ */ n(
              Qe,
              {
                label: "Blacklist",
                helper: "These performers are removed (case-insensitive).",
                values: B("Performers").Blacklist,
                onChange: (r) => {
                  U("Performers", { Blacklist: r });
                },
                placeholder: "Search performers…"
              }
            )
          ]
        }
      ) : null,
      Ge ? /* @__PURE__ */ s(
        H,
        {
          title: "Tags",
          summary: "Separators, limits, sort, and allow/block lists",
          children: [
            /* @__PURE__ */ n(w, { label: "Separator", children: /* @__PURE__ */ n(
              Ve,
              {
                value: B("Tags").Separator,
                onChange: (r) => {
                  U("Tags", { Separator: r });
                },
                options: pt,
                customPlaceholder: "Custom separator"
              }
            ) }),
            /* @__PURE__ */ n(w, { label: "Max count", helper: "0 = unlimited", children: /* @__PURE__ */ n(
              Se,
              {
                value: B("Tags").MaxCount,
                min: 0,
                onChange: (r) => {
                  U("Tags", { MaxCount: r });
                }
              }
            ) }),
            /* @__PURE__ */ n(w, { label: "On overflow", children: /* @__PURE__ */ n(
              ue,
              {
                value: B("Tags").OnOverflow,
                onChange: (r) => {
                  U("Tags", { OnOverflow: r });
                },
                options: mt
              }
            ) }),
            /* @__PURE__ */ n(w, { label: "Sort", children: /* @__PURE__ */ n(
              ue,
              {
                value: B("Tags").Sort,
                onChange: (r) => {
                  U("Tags", { Sort: r });
                },
                options: fa
              }
            ) }),
            /* @__PURE__ */ n(
              we,
              {
                label: "Whitelist",
                helper: "If set, only these tags are kept (case-insensitive).",
                values: B("Tags").Whitelist,
                onChange: (r) => {
                  U("Tags", { Whitelist: r });
                },
                placeholder: "Search tags…"
              }
            ),
            /* @__PURE__ */ n(
              we,
              {
                label: "Blacklist",
                helper: "These tags are removed (case-insensitive).",
                values: B("Tags").Blacklist,
                onChange: (r) => {
                  U("Tags", { Blacklist: r });
                },
                placeholder: "Search tags…"
              }
            )
          ]
        }
      ) : null,
      oe || be ? /* @__PURE__ */ s(
        H,
        {
          title: oe && be ? "Date & duration format" : oe ? "Date format" : "Duration format",
          summary: oe && be ? "How $date and $duration tokens are written" : oe ? "How the $date token is written" : "How the $duration token is written",
          children: [
            oe ? /* @__PURE__ */ n(w, { label: "Date format", helper: "e.g. yyyy-MM-dd", children: /* @__PURE__ */ n(
              Pe,
              {
                value: e.DateFormat,
                onChange: (r) => {
                  v("DateFormat", r);
                },
                options: ga,
                customPlaceholder: "yyyy-MM-dd"
              }
            ) }) : null,
            be ? /* @__PURE__ */ n(w, { label: "Duration format", children: /* @__PURE__ */ n(
              Pe,
              {
                value: e.DurationFormat,
                onChange: (r) => {
                  v("DurationFormat", r);
                },
                options: ba,
                customPlaceholder: "hh\\-mm\\-ss"
              }
            ) }) : null
          ]
        }
      ) : null,
      !qe && !Ge && !oe && !be ? /* @__PURE__ */ n(
        K,
        {
          title: "No token-specific settings needed",
          description: "Add $performers, $tags, $date, or $duration to your filename or folder template to configure how they're formatted.",
          children: /* @__PURE__ */ s("div", { className: "flex flex-wrap gap-1", children: [
            /* @__PURE__ */ n(
              "button",
              {
                type: "button",
                onClick: () => {
                  ge("{ - $performers}");
                },
                className: "cursor-pointer rounded-lg border border-border bg-card px-2 py-1 font-mono text-xs text-foreground hover:border-accent/50 hover:text-accent",
                children: "$performers"
              }
            ),
            /* @__PURE__ */ n(
              "button",
              {
                type: "button",
                onClick: () => {
                  ge("{ - $tags}");
                },
                className: "cursor-pointer rounded-lg border border-border bg-card px-2 py-1 font-mono text-xs text-foreground hover:border-accent/50 hover:text-accent",
                children: "$tags"
              }
            ),
            /* @__PURE__ */ n(
              "button",
              {
                type: "button",
                onClick: () => {
                  ge("{ - $date}");
                },
                className: "cursor-pointer rounded-lg border border-border bg-card px-2 py-1 font-mono text-xs text-foreground hover:border-accent/50 hover:text-accent",
                children: "$date"
              }
            ),
            /* @__PURE__ */ n(
              "button",
              {
                type: "button",
                onClick: () => {
                  ge("{ [$duration]}");
                },
                className: "cursor-pointer rounded-lg border border-border bg-card px-2 py-1 font-mono text-xs text-foreground hover:border-accent/50 hover:text-accent",
                children: "$duration"
              }
            )
          ] })
        }
      ) : null
    ] }),
    /* @__PURE__ */ n(me, { title: "Destination Routing", children: /* @__PURE__ */ s(
      H,
      {
        title: "Destination routing",
        summary: "Per-studio / tag / path destinations, allowed roots, and the default-relocate gate",
        children: [
          /* @__PURE__ */ n(
            K,
            {
              title: "Advanced routing & safety",
              headerRight: /* @__PURE__ */ n(
                L,
                {
                  label: "Enabled",
                  checked: e.EnableAdvancedRouting,
                  onChange: (r) => {
                    v("EnableAdvancedRouting", r);
                  }
                }
              ),
              children: e.EnableAdvancedRouting ? /* @__PURE__ */ s(V, { children: [
                /* @__PURE__ */ n("h4", { className: "text-sm font-semibold text-foreground", children: "Allowed roots" }),
                /* @__PURE__ */ n("p", { className: "mb-4 mt-1 text-sm text-secondary", children: "A rename may only write inside these absolute directories; a target outside them is rejected. Empty = files stay within their own source folder." }),
                /* @__PURE__ */ n(
                  xe,
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
                  Oe,
                  {
                    rows: e.PathDestinations,
                    onChange: (r) => {
                      v("PathDestinations", r);
                    },
                    makeRow: () => ({ Pattern: "", Dest: "", IsRegex: !1 }),
                    renderRow: (r, E, y) => /* @__PURE__ */ s(V, { children: [
                      /* @__PURE__ */ n(w, { label: "Source path", children: /* @__PURE__ */ n(
                        M,
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
                        L,
                        {
                          label: "Match as a regex",
                          checked: r.IsRegex,
                          onChange: (T) => {
                            y({ IsRegex: T });
                          }
                        }
                      ),
                      /* @__PURE__ */ n(Xe, { pattern: r.Pattern, isRegex: r.IsRegex }),
                      /* @__PURE__ */ s(w, { label: "Destination root", children: [
                        /* @__PURE__ */ n(
                          M,
                          {
                            value: r.Dest,
                            onChange: (T) => {
                              y({ Dest: T });
                            },
                            placeholder: "Destination root"
                          }
                        ),
                        /* @__PURE__ */ n(ye, { value: r.Dest })
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
            K,
            {
              title: "Per-studio destinations",
              description: "Pick a studio, then the absolute root its items route to.",
              headerRight: /* @__PURE__ */ n(
                L,
                {
                  label: "Enabled",
                  checked: e.EnableStudioDestinations,
                  onChange: (r) => {
                    v("EnableStudioDestinations", r);
                  }
                }
              ),
              children: e.EnableStudioDestinations ? /* @__PURE__ */ n(
                Dn,
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
            K,
            {
              title: "Per-tag destinations",
              description: "Pick a tag, then the absolute root its items route to.",
              headerRight: /* @__PURE__ */ n(
                L,
                {
                  label: "Enabled",
                  checked: e.EnableTagDestinations,
                  onChange: (r) => {
                    v("EnableTagDestinations", r);
                  }
                }
              ),
              children: e.EnableTagDestinations ? /* @__PURE__ */ n(
                wt,
                {
                  map: e.TagDestinations,
                  onChange: (r) => {
                    v("TagDestinations", r);
                  },
                  renderKey: (r, E, y) => /* @__PURE__ */ n(
                    we,
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
                  renderValue: (r, E) => /* @__PURE__ */ s(V, { children: [
                    /* @__PURE__ */ n(M, { value: r, onChange: E, placeholder: "Destination root" }),
                    /* @__PURE__ */ n(ye, { value: r })
                  ] }),
                  addLabel: "Add tag rule"
                }
              ) : /* @__PURE__ */ n("p", { className: "text-sm text-secondary", children: "Turn this on to add per-tag routing rules." })
            }
          ),
          /* @__PURE__ */ s(K, { title: "Default & unorganized destinations", children: [
            /* @__PURE__ */ s(
              w,
              {
                label: "Default destination",
                helper: "Where an item matching no rule goes. Blank = no default route. Honored only with the relocate gate below ON.",
                children: [
                  /* @__PURE__ */ n(
                    M,
                    {
                      value: e.DefaultDestination,
                      onChange: (r) => {
                        v("DefaultDestination", r);
                      },
                      placeholder: "Absolute root, or blank"
                    }
                  ),
                  /* @__PURE__ */ n(ye, { value: e.DefaultDestination })
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
                    M,
                    {
                      value: e.UnorganizedDestination,
                      onChange: (r) => {
                        v("UnorganizedDestination", r);
                      },
                      placeholder: "Absolute root, or blank"
                    }
                  ),
                  /* @__PURE__ */ n(ye, { value: e.UnorganizedDestination })
                ]
              }
            ),
            /* @__PURE__ */ n(
              L,
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
            K,
            {
              title: "Sidecar files",
              description: "Files sharing the primary's basename with one of these extensions move and rename with it; a target that already exists is left untouched, never overwritten. Captions Cove tracks always move regardless.",
              children: /* @__PURE__ */ s(w, { label: "Also move sidecar files with these extensions", children: [
                /* @__PURE__ */ n(
                  xe,
                  {
                    values: e.AssociatedExtensions,
                    onChange: (r) => {
                      v("AssociatedExtensions", r);
                    },
                    placeholder: "Add an extension, press Enter",
                    normalize: ut,
                    onReject: (r) => !/^[a-z0-9]+$/.test(r),
                    onLiveChange: (r) => {
                      ee(r);
                    }
                  }
                ),
                (() => {
                  const r = cn(
                    ut(X)
                  );
                  return r ? /* @__PURE__ */ n(F, { kind: "warning", children: r }) : null;
                })()
              ] })
            }
          ),
          /* @__PURE__ */ n(K, { title: "Empty source folder", children: /* @__PURE__ */ n(
            L,
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
    /* @__PURE__ */ s(me, { title: "Advanced", children: [
      /* @__PURE__ */ s(
        H,
        {
          title: "Clean up the name",
          summary: "Illegal-character and space handling, case, ASCII",
          children: [
            /* @__PURE__ */ n(w, { label: "Illegal-char replacement", children: /* @__PURE__ */ n(
              Je,
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
              Je,
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
                  M,
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
                options: ha
              }
            ) }),
            /* @__PURE__ */ n(
              L,
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
        H,
        {
          title: "Length & collisions",
          summary: "Length caps, what to drop when too long, duplicate suffix",
          children: [
            /* @__PURE__ */ n(w, { label: "Filename max length", children: /* @__PURE__ */ n(
              Se,
              {
                value: e.FilenameMax,
                min: 1,
                onChange: (r) => {
                  v("FilenameMax", r);
                }
              }
            ) }),
            /* @__PURE__ */ n(w, { label: "Full-path max length", children: /* @__PURE__ */ n(
              Se,
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
                xe,
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
                Ye,
                {
                  tokens: ct,
                  values: e.DropOrder,
                  onAdd: (r) => {
                    v(
                      "DropOrder",
                      e.DropOrder.includes(r) ? e.DropOrder : [...e.DropOrder, r]
                    );
                  }
                }
              ),
              /* @__PURE__ */ n(gt, { values: e.DropOrder })
            ] }),
            /* @__PURE__ */ n(
              w,
              {
                label: "Duplicate suffix format",
                helper: "{n} = a counter added only when a name already exists, e.g. name (1).mp4.",
                children: /* @__PURE__ */ n(
                  Pe,
                  {
                    value: e.DuplicateSuffixFormat,
                    onChange: (r) => {
                      v("DuplicateSuffixFormat", r);
                    },
                    options: xa,
                    customPlaceholder: " ({n})"
                  }
                )
              }
            )
          ]
        }
      ),
      /* @__PURE__ */ s(
        H,
        {
          title: "Excludes",
          summary: "Skip items by tag, studio, or source path — evaluated before any routing",
          children: [
            /* @__PURE__ */ n(
              K,
              {
                title: "Exclude by tag",
                description: "An item carrying any of these tags is skipped — never renamed, never moved. Evaluated before any routing rule.",
                children: /* @__PURE__ */ n(
                  we,
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
              K,
              {
                title: "Exclude by studio",
                description: "An item under any of these studios — or under a child of one — is skipped entirely. Evaluated before any routing rule.",
                children: /* @__PURE__ */ n(
                  Tt,
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
              K,
              {
                title: "Exclude by source path",
                description: "An item whose source path matches a rule is skipped entirely. Evaluated before any routing rule. An exact match or a regex.",
                children: /* @__PURE__ */ n(
                  Oe,
                  {
                    rows: e.ExcludePaths,
                    onChange: (r) => {
                      v("ExcludePaths", r);
                    },
                    makeRow: () => ({ Pattern: "", IsRegex: !1 }),
                    renderRow: (r, E, y) => /* @__PURE__ */ s(V, { children: [
                      /* @__PURE__ */ n(w, { label: "Source path", children: /* @__PURE__ */ n(
                        M,
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
                        L,
                        {
                          label: "Match as a regex",
                          checked: r.IsRegex,
                          onChange: (T) => {
                            y({ IsRegex: T });
                          }
                        }
                      ),
                      /* @__PURE__ */ n(Xe, { pattern: r.Pattern, isRegex: r.IsRegex })
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
        H,
        {
          title: "Field rewriting",
          summary: "Literal token replacements, article stripping, name shaping, and per-token whitespace",
          children: [
            /* @__PURE__ */ n(
              K,
              {
                title: "Per-token replacements",
                description: "A literal find/replace on a single token's value, before the name is shaped. The target is a canonical token name (e.g. studio, title), matched case-insensitively.",
                children: /* @__PURE__ */ n(
                  Oe,
                  {
                    rows: e.FieldReplacers,
                    onChange: (r) => {
                      v("FieldReplacers", r);
                    },
                    makeRow: () => ({ TargetToken: Ce[0].value, Find: "", Replace: "" }),
                    renderRow: (r, E, y) => {
                      const T = Ce.some(($) => $.value === r.TargetToken) ? Ce : [
                        ...Ce,
                        { value: r.TargetToken, label: `${r.TargetToken} (unknown)` }
                      ];
                      return /* @__PURE__ */ s(V, { children: [
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
                          M,
                          {
                            value: r.Find,
                            onChange: ($) => {
                              y({ Find: $ });
                            },
                            placeholder: "Text to find"
                          }
                        ) }),
                        /* @__PURE__ */ n(w, { label: "Replace with", children: /* @__PURE__ */ n(
                          M,
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
            /* @__PURE__ */ s(K, { title: "Strip leading article", children: [
              /* @__PURE__ */ n(
                L,
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
                xe,
                {
                  values: e.Articles,
                  onChange: (r) => {
                    v("Articles", r);
                  },
                  placeholder: "Add article, press Enter"
                }
              ) })
            ] }),
            /* @__PURE__ */ s(K, { title: "Name shaping", children: [
              /* @__PURE__ */ n(
                L,
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
                L,
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
                L,
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
    /* @__PURE__ */ n("div", { id: "rename-undo-section", children: /* @__PURE__ */ n(qn, { refreshKey: 0 }) }),
    /* @__PURE__ */ n(
      ya,
      {
        dirty: I,
        saving: p,
        saveError: u,
        savedFlash: b,
        canSave: C,
        onSave: () => void It(),
        onDiscard: () => {
          a(o);
        }
      }
    )
  ] });
}
function Na() {
  return /* @__PURE__ */ n(Ot, {});
}
function Sa() {
  return /* @__PURE__ */ n(Ot, {});
}
function bt(t) {
  if (!t) return t;
  const e = Math.max(t.lastIndexOf("/"), t.lastIndexOf("\\"));
  return e >= 0 ? t.slice(e + 1) : t;
}
const wa = 5;
function Ta(t) {
  const e = t / 1073741824;
  return e >= 10 ? `${Math.round(e)} GB` : `${e.toFixed(1)} GB`;
}
function Ca(t) {
  return ((t == null ? void 0 : t.volumePairs) ?? []).map(
    (e) => `↪ ${e.count} item${e.count === 1 ? "" : "s"} (${Ta(e.bytes)}) move from ${e.from} to ${e.to}.`
  );
}
function Ea(t) {
  return t === "Heavy" ? "This is a LARGE cross-drive move — files will be COPIED across drives, which can take a while. Click OK only if you are sure; Cancel to stop. You can undo this afterwards." : t === "Standard" ? "This moves files across drives. Click OK to proceed, or Cancel to stop. You can undo this afterwards." : "Click OK to rename, or Cancel to stop. You can undo this afterwards.";
}
function Ra(t, e) {
  const a = t.filter((S) => S.status === "Renamer" || S.status === "Move"), o = a.length, l = t.length, i = t.filter((S) => S.status === "SkipGated").length, c = t.filter((S) => S.status === "SkipCollision").length, h = t.filter((S) => S.status === "SkipLocked").length, m = i + c + h, p = a.filter((S) => S.suffixed).length, g = a.filter((S) => S.sanitized).length, u = [];
  if (m > 0) {
    const S = [];
    if (i > 0 && S.push(`${i} need a required field`), c > 0 && S.push(`${c} have a name conflict`), h > 0 && S.push(`${h} are in use`), S.length === 1) {
      const z = i > 0 ? "needs a required field" : c > 0 ? "name conflict" : "in use";
      u.push(`⚠ ${m} skipped (${z}).`);
    } else
      u.push(`⚠ ${m} skipped — ${S.join(", ")}.`);
  }
  g > 0 && u.push(`⚠ ${g} had illegal characters cleaned up.`), p > 0 && u.push(`⚠ ${p} got a number added to avoid a name clash (e.g. "name (1)").`);
  const f = Ca(e), b = u.length > 0 ? `${u.join(`
`)}

` : "", x = f.length > 0 ? `${f.join(`
`)}

` : "";
  if (o === 0)
    return { text: `Nothing will be renamed — all ${l} selected item${l === 1 ? "" : "s"} are skipped or already named correctly.

` + b + "Click OK to dismiss.", willRenameCount: 0 };
  const d = o === l ? `Rename ${o} selected item${o === 1 ? "" : "s"}?` : `Rename ${o} of ${l} selected items?`, N = a.slice(0, wa).map((S) => {
    const z = bt(S.oldFullPath), te = S.newBasename || bt(S.newFullPath);
    return `  ${z}  →  ${te}`;
  }), j = o - N.length;
  j > 0 && N.push(`  … and ${j} more.`);
  const R = (e == null ? void 0 : e.confirmLevel) ?? "Light", X = Ea(R);
  return { text: `${d}

` + b + x + `Examples:
${N.join(`
`)}

` + X, willRenameCount: o };
}
const Ft = "com.alextomas955.renamer", $a = `/extensions/${Ft}/preview`, Aa = `/extensions/${Ft}/rename`;
async function Da(t, e) {
  const a = JSON.stringify({ EntityType: e.entityType, EntityIds: e.entityIds }), o = await A($a, { method: "POST", body: a }), { text: l, willRenameCount: i } = Ra(o.items, o.summary);
  if (!window.confirm(l))
    return { cancelled: !0 };
  if (i === 0)
    return { cancelled: !0 };
  try {
    await A(Aa, { method: "POST", body: a });
  } catch (c) {
    if (c instanceof Z) throw c;
  }
  return {};
}
const Pa = { components: { RenameSettingsPanel: Na, RenamePage: Sa } };
Pa.actionHandlers = { renameSelected: Da };
export {
  Pa as default
};
