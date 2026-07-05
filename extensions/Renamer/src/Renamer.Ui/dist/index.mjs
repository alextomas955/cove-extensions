import * as e from "react";
import { useCallback as t, useEffect as n, useId as r, useMemo as i, useRef as a, useState as o } from "react";
import { AlertTriangle as s, ArrowDown as c, ArrowUp as l, ChevronDown as u, ChevronUp as d, Loader2 as f, Search as p, Undo2 as m, X as h } from "lucide-react";
import { Fragment as g, jsx as _, jsxs as v } from "react/jsx-runtime";
import { flushSync as y } from "react-dom";
//#region node_modules/@cove/extension-sdk/dist/define.js
function b(e) {
	return e;
}
//#endregion
//#region node_modules/@cove/extension-sdk/dist/api.js
var x = "/api";
async function S(e, t = {}) {
	let n = `${x}${e}`, r = await fetch(n, {
		...t,
		headers: {
			"Content-Type": "application/json",
			...t.headers
		}
	});
	if (!r.ok) {
		let t = await r.text().catch(() => "");
		throw new C(r.status, t || r.statusText, e);
	}
	if (r.status !== 204) return r.json();
}
var C = class extends Error {
	status;
	body;
	path;
	constructor(e, t, n) {
		super(`API ${e} ${n}: ${t}`), this.status = e, this.body = t, this.path = n, this.name = "ApiError";
	}
};
function w(e) {
	let t = `/extensions/${e}/data`;
	return {
		get: (e) => S(`${t}/${encodeURIComponent(e)}`).then((e) => e.value),
		set: (e, n) => S(t, {
			method: "POST",
			body: JSON.stringify({
				key: e,
				value: n
			})
		}),
		delete: (e) => S(`${t}/${encodeURIComponent(e)}`, { method: "DELETE" }),
		getAll: () => S(`${t}`)
	};
}
//#endregion
//#region node_modules/@cove/extension-sdk/dist/hooks.js
function T(e) {
	return i(() => w(e), [e]);
}
//#endregion
//#region src/options.ts
var E = {
	FilenameTemplate: "{$date - }$title{ [$resolution]}",
	FolderTemplate: "",
	DateFormat: "yyyy-MM-dd",
	DurationFormat: "hh\\-mm\\-ss",
	Performers: {
		Separator: " ",
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
	RemoveCharacters: ",#",
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
	Articles: [
		"The",
		"A",
		"An"
	],
	PreventTitlePerformer: !1,
	PreventConsecutiveSegments: !0
};
function D() {
	return {
		...E,
		Performers: {
			...E.Performers,
			Whitelist: [],
			Blacklist: [],
			IgnoreGenders: [],
			GenderOrder: []
		},
		Tags: {
			...E.Tags,
			Whitelist: [],
			Blacklist: [],
			IgnoreGenders: [],
			GenderOrder: []
		},
		DropOrder: [...E.DropOrder],
		RequiredFields: [...E.RequiredFields],
		StudioDestinations: { ...E.StudioDestinations },
		TagDestinations: { ...E.TagDestinations },
		PathDestinations: E.PathDestinations.map((e) => ({ ...e })),
		ExcludeTags: [...E.ExcludeTags],
		ExcludeStudioIds: [...E.ExcludeStudioIds],
		ExcludePaths: E.ExcludePaths.map((e) => ({ ...e })),
		AllowedRoots: [...E.AllowedRoots],
		AssociatedExtensions: [...E.AssociatedExtensions],
		FieldReplacers: E.FieldReplacers.map((e) => ({ ...e })),
		Articles: [...E.Articles]
	};
}
function O(e) {
	return e && typeof e == "object" ? e : {};
}
function k(e, t) {
	return typeof e == "string" ? e : t;
}
function A(e, t) {
	return typeof e == "number" && Number.isFinite(e) ? e : t;
}
function j(e, t) {
	return typeof e == "boolean" ? e : t;
}
function M(e, t) {
	return Array.isArray(e) ? e.filter((e) => typeof e == "string") : t;
}
function N(e, t) {
	return Array.isArray(e) ? e.filter((e) => typeof e == "number" && Number.isFinite(e)) : t;
}
function P(e) {
	let t = O(e), n = {};
	for (let [e, r] of Object.entries(t)) {
		let t = Number(e);
		Number.isInteger(t) && typeof r == "string" && (n[t] = r);
	}
	return n;
}
function ee(e) {
	let t = O(e), n = {};
	for (let [e, r] of Object.entries(t)) typeof r == "string" && (n[e] = r);
	return n;
}
function F(e) {
	return Array.isArray(e) ? e.filter((e) => e && typeof e == "object").map((e) => {
		let t = e;
		return {
			Pattern: k(t.Pattern, ""),
			Dest: k(t.Dest, ""),
			IsRegex: j(t.IsRegex, !1)
		};
	}) : [];
}
function te(e) {
	return Array.isArray(e) ? e.filter((e) => e && typeof e == "object").map((e) => {
		let t = e;
		return {
			Pattern: k(t.Pattern, ""),
			IsRegex: j(t.IsRegex, !1)
		};
	}) : [];
}
function ne(e) {
	return Array.isArray(e) ? e.filter((e) => e && typeof e == "object").map((e) => {
		let t = e;
		return {
			TargetToken: k(t.TargetToken, ""),
			Find: k(t.Find, ""),
			Replace: k(t.Replace, "")
		};
	}) : [];
}
function re(e) {
	return e === "KeepFirst" ? "KeepFirst" : "DropAll";
}
function ie(e) {
	return e === "None" || e === "IdAsc" || e === "FavoriteFirst" ? e : "NameAsc";
}
function ae(e) {
	return e === "Lower" || e === "Title" ? e : "None";
}
function oe(e, t) {
	let n = O(e);
	return {
		Separator: k(n.Separator, t.Separator),
		MaxCount: A(n.MaxCount, t.MaxCount),
		OnOverflow: re(n.OnOverflow),
		Sort: ie(n.Sort),
		Whitelist: M(n.Whitelist, []),
		Blacklist: M(n.Blacklist, []),
		IgnoreGenders: M(n.IgnoreGenders, []),
		GenderOrder: M(n.GenderOrder, [])
	};
}
var se = new Set(Object.keys(E));
function ce(e) {
	if (!e || typeof e != "object") return {};
	let t = {};
	for (let [n, r] of Object.entries(e)) se.has(n) || (t[n] = r);
	return t;
}
function le(e) {
	if (!e || typeof e != "object") return D();
	let t = e, n = E;
	return {
		FilenameTemplate: k(t.FilenameTemplate, n.FilenameTemplate),
		FolderTemplate: k(t.FolderTemplate, n.FolderTemplate),
		DateFormat: k(t.DateFormat, n.DateFormat),
		DurationFormat: k(t.DurationFormat, n.DurationFormat),
		Performers: oe(t.Performers, n.Performers),
		Tags: oe(t.Tags, n.Tags),
		IllegalReplacement: k(t.IllegalReplacement, n.IllegalReplacement),
		SpaceReplacement: k(t.SpaceReplacement, n.SpaceReplacement),
		RemoveCharacters: k(t.RemoveCharacters, n.RemoveCharacters),
		Case: ae(t.Case),
		AsciiTransliterate: j(t.AsciiTransliterate, n.AsciiTransliterate),
		FilenameMax: A(t.FilenameMax, n.FilenameMax),
		FullPathMax: A(t.FullPathMax, n.FullPathMax),
		DropOrder: M(t.DropOrder, [...n.DropOrder]),
		OnlyOrganized: j(t.OnlyOrganized, n.OnlyOrganized),
		FilenameAsTitle: j(t.FilenameAsTitle, n.FilenameAsTitle),
		RequiredFields: M(t.RequiredFields, [...n.RequiredFields]),
		DuplicateSuffixFormat: k(t.DuplicateSuffixFormat, n.DuplicateSuffixFormat),
		AutoRenamerOnUpdate: j(t.AutoRenamerOnUpdate, n.AutoRenamerOnUpdate),
		StudioDestinations: P(t.StudioDestinations),
		TagDestinations: ee(t.TagDestinations),
		PathDestinations: F(t.PathDestinations),
		ExcludeTags: M(t.ExcludeTags, []),
		ExcludeStudioIds: N(t.ExcludeStudioIds, []),
		ExcludePaths: te(t.ExcludePaths),
		AllowedRoots: M(t.AllowedRoots, []),
		AssociatedExtensions: M(t.AssociatedExtensions, [...n.AssociatedExtensions]),
		DefaultDestination: k(t.DefaultDestination, n.DefaultDestination),
		UnorganizedDestination: k(t.UnorganizedDestination, n.UnorganizedDestination),
		EnableDefaultRelocate: j(t.EnableDefaultRelocate, n.EnableDefaultRelocate),
		EnableStudioDestinations: j(t.EnableStudioDestinations, n.EnableStudioDestinations),
		EnableTagDestinations: j(t.EnableTagDestinations, n.EnableTagDestinations),
		EnableAdvancedRouting: j(t.EnableAdvancedRouting, n.EnableAdvancedRouting),
		RemoveEmptyFolder: j(t.RemoveEmptyFolder, n.RemoveEmptyFolder),
		SqueezeStudioNames: j(t.SqueezeStudioNames, n.SqueezeStudioNames),
		FieldReplacers: ne(t.FieldReplacers),
		StripLeadingArticles: j(t.StripLeadingArticles, n.StripLeadingArticles),
		Articles: M(t.Articles, [...n.Articles]),
		PreventTitlePerformer: j(t.PreventTitlePerformer, n.PreventTitlePerformer),
		PreventConsecutiveSegments: j(t.PreventConsecutiveSegments, n.PreventConsecutiveSegments)
	};
}
//#endregion
//#region src/primitivesLogic.ts
function ue(e) {
	if (e.length === 0) return { valid: !0 };
	try {
		return new RegExp(e), { valid: !0 };
	} catch (e) {
		return {
			valid: !1,
			message: e instanceof Error ? e.message : String(e)
		};
	}
}
function de(e) {
	let t = e.trim();
	return t.length === 0 ? !0 : /^[A-Za-z]:[\\/]/.test(t) || /^[\\/]/.test(t);
}
var fe = /* @__PURE__ */ new Set([
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
function pe(e) {
	return e.length === 0 ? null : /^[a-z0-9]+$/.test(e) ? fe.has(e) ? "This looks like a primary media extension, not a sidecar." : null : "Extensions are letters and numbers only, like srt or nfo.";
}
//#endregion
//#region src/entityPickerLogic.ts
function I(e, t) {
	let n = e.trim().toLowerCase();
	return n.length === 0 ? [...t] : t.filter((e) => e.name.toLowerCase().includes(n));
}
function me(e, t, n) {
	if (t.length === 0) return [...e];
	let r = new Set(t);
	return e.filter((e) => !r.has(n(e)));
}
function he(e, t) {
	let n = new Set(t);
	return e.filter((e) => !n.has(e.value));
}
function ge(e, t) {
	let n = t.find((t) => t.id === e);
	return n ? n.name : `#${e} (missing)`;
}
function _e(e, t) {
	return t.some((t) => t.id === e);
}
function ve(e, t) {
	let n = e.trim(), r = t.find((e) => e.name.toLowerCase() === n.toLowerCase());
	return r ? r.name : n;
}
//#endregion
//#region src/primitives.tsx
var L = "w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none", R = "cursor-pointer rounded-lg border px-2 py-1 text-xs", z = "border-border bg-card text-foreground hover:border-accent/50 hover:text-accent", B = "border-accent bg-accent/15 text-foreground";
function ye(e) {
	return `${R} ${e ? B : z}`;
}
function V({ selected: e, onClick: t, disabled: n, title: r, mono: i, children: a }) {
	return /* @__PURE__ */ _("button", {
		type: "button",
		onClick: t,
		disabled: n,
		title: r,
		className: i ? `${ye(e)} font-mono` : ye(e),
		children: a
	});
}
var be = "__custom__";
function H({ label: e, helper: t, children: n }) {
	return /* @__PURE__ */ v("label", {
		className: "block text-sm",
		title: t,
		children: [
			e ? /* @__PURE__ */ _("span", {
				className: "mb-1 block text-xs font-medium uppercase tracking-wide text-muted",
				children: e
			}) : null,
			n,
			t ? /* @__PURE__ */ _("span", {
				className: "mt-1 block text-xs text-secondary",
				children: t
			}) : null
		]
	});
}
function U({ value: e, onChange: t, onFocus: n, placeholder: r, mono: i = !1, inputRef: a }) {
	return /* @__PURE__ */ _("input", {
		ref: a,
		type: "text",
		value: e,
		placeholder: r,
		onChange: (e) => {
			t(e.target.value);
		},
		onFocus: n,
		className: i ? `${L} font-mono` : L
	});
}
function xe({ value: e, onChange: t, min: n }) {
	return /* @__PURE__ */ _("input", {
		type: "number",
		value: Number.isNaN(e) ? "" : e,
		min: n,
		onChange: (e) => {
			t(e.target.value === "" ? 0 : Number(e.target.value));
		},
		className: `themed-number-input ${L}`
	});
}
function W({ value: e, onChange: t, options: n }) {
	return /* @__PURE__ */ _("select", {
		value: e,
		onChange: (e) => {
			t(e.target.value);
		},
		className: L,
		children: n.map((e) => /* @__PURE__ */ _("option", {
			value: e.value,
			children: e.label
		}, e.value))
	});
}
function Se({ value: e, onChange: t, options: n, customPlaceholder: r }) {
	let i = n.find((t) => t.value === e), a = i === void 0, o = a ? be : e, s = i ? `${i.value} → ${i.example}` : e;
	return /* @__PURE__ */ v("div", { children: [/* @__PURE__ */ v("select", {
		value: o,
		onChange: (e) => {
			let n = e.target.value;
			n === be ? a || t("") : t(n);
		},
		className: L,
		children: [n.map((e) => /* @__PURE__ */ v("option", {
			value: e.value,
			children: [
				e.value,
				" → ",
				e.example
			]
		}, e.value)), /* @__PURE__ */ _("option", {
			value: be,
			children: "Custom…"
		})]
	}), a ? /* @__PURE__ */ _("div", {
		className: "mt-2",
		children: /* @__PURE__ */ _(U, {
			value: e,
			onChange: t,
			placeholder: r,
			mono: !0
		})
	}) : /* @__PURE__ */ _("span", {
		className: "mt-1 block font-mono text-xs text-secondary",
		children: s
	})] });
}
function Ce({ value: e, onChange: t, options: n, customPlaceholder: r }) {
	let i = !n.some((t) => t.value === e);
	return /* @__PURE__ */ v("div", { children: [/* @__PURE__ */ v("div", {
		className: "flex flex-wrap gap-1",
		children: [n.map((n) => /* @__PURE__ */ _(V, {
			selected: n.value === e,
			onClick: () => {
				t(n.value);
			},
			children: n.label
		}, n.value || "__empty__")), /* @__PURE__ */ _(V, {
			selected: i,
			onClick: () => {
				i || t("");
			},
			children: "Custom"
		})]
	}), i ? /* @__PURE__ */ _("div", {
		className: "mt-2",
		children: /* @__PURE__ */ _(U, {
			value: e,
			onChange: t,
			placeholder: r,
			mono: !0
		})
	}) : null] });
}
function we({ value: e, onChange: t, stripLabel: r, replaceLabel: i, stripHelper: s, replaceHelper: c, inputPlaceholder: l }) {
	let u = a(null), [d, f] = o(e !== ""), p = a(e);
	n(() => {
		e === "" ? p.current !== "" && f(!1) : f(!0), p.current = e;
	}, [e]);
	let m = d || e !== "";
	function h() {
		f(!1), e !== "" && t("");
	}
	function g() {
		f(!0), requestAnimationFrame(() => u.current?.focus());
	}
	return /* @__PURE__ */ v("div", { children: [/* @__PURE__ */ v("div", {
		className: "flex gap-1",
		children: [/* @__PURE__ */ _(V, {
			selected: !m,
			onClick: h,
			children: r
		}), /* @__PURE__ */ _(V, {
			selected: m,
			onClick: g,
			children: i
		})]
	}), m ? /* @__PURE__ */ v("div", {
		className: "mt-2",
		children: [/* @__PURE__ */ _(U, {
			value: e,
			onChange: t,
			placeholder: l,
			inputRef: u,
			mono: !0
		}), c ? /* @__PURE__ */ _("span", {
			className: "mt-1 block text-xs text-secondary",
			children: c
		}) : null]
	}) : s ? /* @__PURE__ */ _("span", {
		className: "mt-1 block text-xs text-secondary",
		children: s
	}) : null] });
}
function G({ label: e, checked: t, onChange: n, helper: r, ariaLabel: i }) {
	return /* @__PURE__ */ v("div", { children: [/* @__PURE__ */ v("label", {
		className: "flex items-center gap-2 text-sm text-secondary",
		title: r,
		children: [/* @__PURE__ */ _("button", {
			type: "button",
			role: "switch",
			"aria-checked": t,
			"aria-label": e ? void 0 : i,
			onClick: () => {
				n(!t);
			},
			className: `inline-flex h-5 w-9 items-center rounded-full transition-colors ${t ? "bg-accent" : "bg-card border border-border"}`,
			children: /* @__PURE__ */ _("span", {
				className: "inline-block h-4 w-4 rounded-full bg-white transition-transform",
				style: { transform: t ? "translateX(1rem)" : "translateX(0.125rem)" }
			})
		}), e ? /* @__PURE__ */ _("span", { children: e }) : null]
	}), r ? /* @__PURE__ */ _("p", {
		className: "mt-1 text-xs text-secondary",
		children: r
	}) : null] });
}
function Te({ values: e, onChange: t, placeholder: n, ordered: i = !1, normalize: a, onReject: o, onLiveChange: s }) {
	let c = r();
	function l(n) {
		let r = (a ? a(n.value) : n.value).trim();
		r.length !== 0 && (o?.(r) || (e.includes(r) || t([...e, r]), n.value = ""));
	}
	function u(n) {
		t(e.filter((e, t) => t !== n));
	}
	function d(n, r) {
		let i = n + r;
		if (i < 0 || i >= e.length) return;
		let a = [...e];
		[a[n], a[i]] = [a[i], a[n]], t(a);
	}
	return /* @__PURE__ */ v("div", { children: [e.length > 0 ? /* @__PURE__ */ _("div", {
		className: "mb-1 flex flex-wrap gap-1",
		children: e.map((e, t) => /* @__PURE__ */ v("span", {
			className: "inline-flex items-center gap-1 rounded-lg border border-border bg-card px-2 py-0.5 text-xs text-foreground",
			children: [
				i ? /* @__PURE__ */ v(g, { children: [/* @__PURE__ */ _("button", {
					type: "button",
					"aria-label": `Move ${e} up`,
					onClick: () => {
						d(t, -1);
					},
					className: "text-muted hover:text-foreground",
					children: "↑"
				}), /* @__PURE__ */ _("button", {
					type: "button",
					"aria-label": `Move ${e} down`,
					onClick: () => {
						d(t, 1);
					},
					className: "text-muted hover:text-foreground",
					children: "↓"
				})] }) : null,
				/* @__PURE__ */ _("span", {
					className: "font-mono",
					children: e
				}),
				/* @__PURE__ */ _("button", {
					type: "button",
					"aria-label": `Remove ${e}`,
					onClick: () => {
						u(t);
					},
					className: "text-muted hover:text-foreground",
					children: /* @__PURE__ */ _(h, { className: "h-3 w-3" })
				})
			]
		}, `${e}-${t}`))
	}) : null, /* @__PURE__ */ _("input", {
		id: c,
		type: "text",
		placeholder: n,
		className: L,
		onChange: (e) => {
			s?.(e.target.value);
		},
		onKeyDown: (e) => {
			e.key === "Enter" && (e.preventDefault(), l(e.currentTarget));
		},
		onBlur: (e) => {
			l(e.currentTarget);
		}
	})] });
}
function Ee({ options: e, values: t, onChange: n }) {
	let r = new Set(e.map((e) => e.value)), i = t.filter((e) => !r.has(e));
	function a(r) {
		let a = t.includes(r);
		n([...e.map((e) => e.value).filter((e) => e === r ? !a : t.includes(e)), ...i]);
	}
	function o(e) {
		n(t.filter((t) => t !== e));
	}
	return /* @__PURE__ */ v("div", {
		className: "flex flex-wrap gap-1",
		children: [e.map((e) => /* @__PURE__ */ _(V, {
			selected: t.includes(e.value),
			onClick: () => {
				a(e.value);
			},
			children: e.label
		}, e.value)), i.map((e) => /* @__PURE__ */ v("button", {
			type: "button",
			onClick: () => {
				o(e);
			},
			className: `${ye(!0)} inline-flex items-center gap-1`,
			title: "Not a recognized value — click to remove",
			children: [e, /* @__PURE__ */ _(h, { className: "h-3 w-3" })]
		}, `extra:${e}`))]
	});
}
function De({ options: e, values: t, onChange: n, addPrompt: r }) {
	let i = (t) => e.find((e) => e.value === t)?.label ?? t, a = he(e, t);
	function o(e, r) {
		let i = e + r;
		if (i < 0 || i >= t.length) return;
		let a = [...t];
		[a[e], a[i]] = [a[i], a[e]], n(a);
	}
	function s(e) {
		n(t.filter((t, n) => n !== e));
	}
	return /* @__PURE__ */ v("div", { children: [t.length > 0 ? /* @__PURE__ */ _("div", {
		className: "mb-1 flex flex-wrap gap-1",
		children: t.map((e, t) => /* @__PURE__ */ v("span", {
			className: "inline-flex items-center gap-1 rounded-lg border border-border bg-card px-2 py-0.5 text-xs text-foreground",
			children: [
				/* @__PURE__ */ _("button", {
					type: "button",
					"aria-label": `Move ${i(e)} up`,
					onClick: () => {
						o(t, -1);
					},
					className: "text-muted hover:text-foreground",
					children: "↑"
				}),
				/* @__PURE__ */ _("button", {
					type: "button",
					"aria-label": `Move ${i(e)} down`,
					onClick: () => {
						o(t, 1);
					},
					className: "text-muted hover:text-foreground",
					children: "↓"
				}),
				/* @__PURE__ */ _("span", { children: i(e) }),
				/* @__PURE__ */ _("button", {
					type: "button",
					"aria-label": `Remove ${i(e)}`,
					onClick: () => {
						s(t);
					},
					className: "text-muted hover:text-foreground",
					children: /* @__PURE__ */ _(h, { className: "h-3 w-3" })
				})
			]
		}, e))
	}) : null, a.length > 0 ? /* @__PURE__ */ v("select", {
		value: "",
		onChange: (e) => {
			let r = e.target.value;
			r !== "" && n([...t, r]);
		},
		className: L,
		children: [/* @__PURE__ */ _("option", {
			value: "",
			children: r
		}), a.map((e) => /* @__PURE__ */ _("option", {
			value: e.value,
			children: e.label
		}, e.value))]
	}) : null] });
}
function Oe({ tokens: e, values: t, onAdd: n }) {
	return /* @__PURE__ */ v("div", {
		className: "mt-1",
		children: [/* @__PURE__ */ _("span", {
			className: "mb-1 block text-xs text-muted",
			children: "Add a token:"
		}), /* @__PURE__ */ _("div", {
			className: "flex flex-wrap gap-1",
			children: e.map((e) => t.includes(e) ? /* @__PURE__ */ _("button", {
				type: "button",
				disabled: !0,
				className: `${R} border-border bg-card text-muted font-mono`,
				children: e
			}, e) : /* @__PURE__ */ _(V, {
				selected: !1,
				mono: !0,
				onClick: () => {
					n(e);
				},
				children: e
			}, e))
		})]
	});
}
function ke({ rows: e, onChange: t, makeRow: r, renderRow: i, addLabel: s, ordered: c = !1 }) {
	let [l, f] = o(() => e.map((e, t) => t)), p = a(e.length);
	n(() => {
		l.length !== e.length && (p.current = e.length, f(e.map((e, t) => t)));
	}, [e, l.length]);
	function m(n, r) {
		t(e.map((e, t) => t === n ? {
			...e,
			...r
		} : e));
	}
	function g(n) {
		t(e.filter((e, t) => t !== n)), f((e) => e.filter((e, t) => t !== n));
	}
	function y(n, r) {
		let i = n + r;
		if (i < 0 || i >= e.length) return;
		let a = [...e];
		[a[n], a[i]] = [a[i], a[n]], t(a), f((e) => {
			let t = [...e];
			return [t[n], t[i]] = [t[i], t[n]], t;
		});
	}
	function b() {
		t([...e, r()]), f((e) => [...e, p.current++]);
	}
	return /* @__PURE__ */ v("div", {
		className: "space-y-2",
		children: [e.map((t, n) => /* @__PURE__ */ v("div", {
			className: "flex items-start gap-2 rounded-xl border border-border bg-card p-3",
			children: [
				/* @__PURE__ */ _("div", {
					className: "min-w-0 flex-1 space-y-2",
					children: i(t, n, (e) => {
						m(n, e);
					})
				}),
				c ? /* @__PURE__ */ v("span", {
					className: "flex flex-col text-muted",
					children: [/* @__PURE__ */ _("button", {
						type: "button",
						"aria-label": `Move row ${n + 1} up`,
						onClick: () => {
							y(n, -1);
						},
						className: "hover:text-foreground",
						children: /* @__PURE__ */ _(d, { className: "h-4 w-4" })
					}), /* @__PURE__ */ _("button", {
						type: "button",
						"aria-label": `Move row ${n + 1} down`,
						onClick: () => {
							y(n, 1);
						},
						className: "hover:text-foreground",
						children: /* @__PURE__ */ _(u, { className: "h-4 w-4" })
					})]
				}) : null,
				/* @__PURE__ */ _("button", {
					type: "button",
					"aria-label": `Remove row ${n + 1}`,
					onClick: () => {
						g(n);
					},
					className: "text-muted hover:text-foreground",
					children: /* @__PURE__ */ _(h, { className: "h-4 w-4" })
				})
			]
		}, l.length === e.length ? l[n] : n)), /* @__PURE__ */ _(J, {
			variant: "ghost",
			onClick: b,
			children: s
		})]
	});
}
function Ae({ map: e, onChange: t, renderKey: n, renderValue: r, renderKeyLabel: i, addLabel: a }) {
	let [s, c] = o(""), [l, u] = o(""), d = Object.keys(e);
	function f(n, r) {
		t({
			...e,
			[n]: r
		});
	}
	function p(n) {
		t(Object.fromEntries(Object.entries(e).filter(([e]) => e !== n)));
	}
	function m() {
		let n = s.trim();
		n.length === 0 || n in e || (t({
			...e,
			[n]: l
		}), c(""), u(""));
	}
	let g = s.trim().length > 0 && s.trim() in e;
	return /* @__PURE__ */ v("div", {
		className: "space-y-2",
		children: [
			d.map((t) => /* @__PURE__ */ v("div", {
				className: "flex items-center gap-2 rounded-xl border border-border bg-card p-3",
				children: [
					/* @__PURE__ */ _("span", {
						className: "min-w-0 flex-1 truncate font-mono text-sm text-foreground",
						children: i ? i(t) : t
					}),
					/* @__PURE__ */ _("span", {
						className: "flex-1",
						children: r(e[t], (e) => {
							f(t, e);
						})
					}),
					/* @__PURE__ */ _("button", {
						type: "button",
						"aria-label": `Remove ${t}`,
						onClick: () => {
							p(t);
						},
						className: "text-muted hover:text-foreground",
						children: /* @__PURE__ */ _(h, { className: "h-4 w-4" })
					})
				]
			}, t)),
			/* @__PURE__ */ v("div", {
				className: "flex items-start gap-2 rounded-xl border border-border bg-card p-3",
				children: [
					/* @__PURE__ */ _("span", {
						className: "min-w-0 flex-1",
						children: n(s, c, d)
					}),
					/* @__PURE__ */ _("span", {
						className: "min-w-0 flex-1",
						children: r(l, u)
					}),
					/* @__PURE__ */ _(J, {
						onClick: m,
						disabled: s.trim().length === 0 || g,
						children: a
					})
				]
			}),
			g ? /* @__PURE__ */ _(Y, {
				kind: "error",
				children: "That key already has a value."
			}) : null
		]
	});
}
function je({ pattern: e, isRegex: t }) {
	if (!t) return null;
	let n = ue(e);
	return n.valid ? null : /* @__PURE__ */ v(Y, {
		kind: "error",
		children: ["Invalid pattern: ", n.message]
	});
}
function Me({ value: e }) {
	return e.trim().length === 0 || de(e) ? null : /* @__PURE__ */ _(Y, {
		kind: "warning",
		children: "Doesn't look like an absolute path."
	});
}
function K({ title: e, description: t, headerRight: n, children: r }) {
	return /* @__PURE__ */ v("div", {
		className: "rounded-xl border border-border bg-card p-4",
		children: [
			n ? /* @__PURE__ */ v("div", {
				className: "flex items-center justify-between gap-4",
				children: [/* @__PURE__ */ _("h3", {
					className: "text-base font-semibold text-foreground",
					children: e
				}), n]
			}) : /* @__PURE__ */ _("h3", {
				className: "text-base font-semibold text-foreground",
				children: e
			}),
			t ? /* @__PURE__ */ _("p", {
				className: "mb-4 mt-1 text-sm text-secondary",
				children: t
			}) : /* @__PURE__ */ _("div", { className: "mb-4" }),
			/* @__PURE__ */ _("div", {
				className: "space-y-4",
				children: r
			})
		]
	});
}
function Ne({ children: e, mono: t = !1 }) {
	return /* @__PURE__ */ _("span", {
		className: `inline-flex items-center rounded-md px-2 py-0.5 text-xs font-semibold border border-accent/40 bg-accent/15 text-accent ${t ? "font-mono" : "uppercase tracking-wider"}`,
		children: e
	});
}
function Pe({ title: e, hint: t }) {
	return /* @__PURE__ */ v("div", {
		className: "flex items-center gap-3",
		children: [
			/* @__PURE__ */ _("h2", {
				className: "text-xs font-bold uppercase tracking-wider text-secondary",
				children: e
			}),
			/* @__PURE__ */ _("div", { className: "h-px flex-1 bg-border" }),
			t ? /* @__PURE__ */ _("span", {
				className: "text-xs text-muted",
				children: t
			}) : null
		]
	});
}
function q({ title: e, description: t, badge: n, headerRight: r, accent: i = !1, children: a }) {
	let o = i ? "border-accent/30" : "border-border", s = !!e || n != null || r != null;
	return /* @__PURE__ */ v("section", {
		className: `overflow-hidden rounded-2xl border ${o} bg-surface shadow-sm`,
		children: [s ? /* @__PURE__ */ v("div", {
			className: "flex items-start gap-3 border-b border-border px-5 py-4",
			children: [
				n ? /* @__PURE__ */ _("span", {
					className: "mt-0.5",
					children: n
				}) : null,
				/* @__PURE__ */ v("div", {
					className: "min-w-0 flex-1",
					children: [e ? /* @__PURE__ */ _("h3", {
						className: "text-base font-semibold text-foreground",
						children: e
					}) : null, t ? /* @__PURE__ */ _("p", {
						className: "mt-1 text-sm text-secondary",
						children: t
					}) : null]
				}),
				r ? /* @__PURE__ */ _("div", {
					className: "shrink-0",
					children: r
				}) : null
			]
		}) : null, /* @__PURE__ */ _("div", {
			className: "space-y-4 p-5",
			children: a
		})]
	});
}
function Fe({ title: e, description: t, enabled: n, onToggle: r, children: i }) {
	return /* @__PURE__ */ v("section", {
		className: "overflow-hidden rounded-2xl border border-border bg-surface shadow-sm",
		children: [/* @__PURE__ */ v("div", {
			className: "flex items-center gap-3 px-5 py-4",
			children: [/* @__PURE__ */ v("div", {
				className: "min-w-0 flex-1",
				children: [/* @__PURE__ */ _("h3", {
					className: "text-base font-semibold text-foreground",
					children: e
				}), t ? /* @__PURE__ */ _("p", {
					className: "mt-1 text-sm text-secondary",
					children: t
				}) : null]
			}), /* @__PURE__ */ _("div", {
				className: "shrink-0",
				children: /* @__PURE__ */ _(G, {
					label: "",
					ariaLabel: `Enable ${e}`,
					checked: n,
					onChange: r
				})
			})]
		}), n ? /* @__PURE__ */ _("div", {
			className: "space-y-4 border-t border-border px-5 pb-5 pt-4",
			children: i
		}) : null]
	});
}
function Ie({ title: e, summary: t, defaultOpen: n = !1, children: r }) {
	let [i, a] = o(n);
	return /* @__PURE__ */ v("div", {
		className: "overflow-hidden rounded-xl border border-border",
		children: [/* @__PURE__ */ v("button", {
			type: "button",
			onClick: () => {
				a((e) => !e);
			},
			"aria-expanded": i,
			className: "flex w-full items-center justify-between gap-4 bg-card px-4 py-3 text-left transition-colors hover:bg-card-hover",
			children: [/* @__PURE__ */ v("span", {
				className: "min-w-0",
				children: [/* @__PURE__ */ _("span", {
					className: "block text-sm font-medium text-foreground",
					children: e
				}), t ? /* @__PURE__ */ _("span", {
					className: "mt-1 block truncate text-xs text-muted",
					children: t
				}) : null]
			}), _(i ? d : u, { className: "h-4 w-4 shrink-0 text-muted" })]
		}), i ? /* @__PURE__ */ _("div", {
			className: "space-y-4 border-t border-border px-4 py-4",
			children: r
		}) : null]
	});
}
function J({ variant: e = "primary", children: t, onClick: n, disabled: r }) {
	return /* @__PURE__ */ _("button", {
		type: "button",
		onClick: n,
		disabled: r,
		className: e === "ghost" ? "inline-flex items-center gap-1.5 rounded-lg border border-border bg-card px-3 py-2 text-sm font-medium text-secondary hover:border-accent/50 hover:bg-card-hover hover:text-foreground disabled:opacity-60" : "inline-flex items-center gap-2 rounded-lg bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-60",
		children: t
	});
}
function Y({ kind: e, children: t }) {
	return /* @__PURE__ */ _("span", {
		className: `text-xs ${e === "success" ? "text-green-400" : e === "error" ? "text-red-400" : e === "warning" ? "text-amber-400" : "text-secondary"}`,
		children: t
	});
}
function X() {
	return /* @__PURE__ */ _(f, { className: "h-4 w-4 animate-spin" });
}
//#endregion
//#region src/entityPicker.tsx
var Le = "com.alextomas955.renamer", Z = `/extensions/${Le}/list-studios`, Q = `/extensions/${Le}/list-tags`, Re = `/extensions/${Le}/list-performers`, ze = "w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none", Be = "cursor-pointer rounded-lg px-2 py-1 text-left text-sm text-foreground hover:bg-card-hover", Ve = "inline-flex items-center gap-1 rounded-lg border border-border bg-card px-2 py-0.5 text-xs text-foreground", He = "border-red-400 text-red-400";
function Ue({ label: e, helper: i, values: s, onChange: c, endpointPath: l, adapter: u, placeholder: d, excludeValues: f }) {
	let p = r(), [m, g] = o(""), [y, b] = o(!1), [x, C] = o([]), [w, T] = o(!1), [E, D] = o(!1), [O, k] = o(!1), A = a(!1), j = a(null);
	n(() => {
		if (!y) return;
		let e = (e) => {
			j.current?.contains(e.target) || b(!1);
		};
		return document.addEventListener("mousedown", e), () => {
			document.removeEventListener("mousedown", e);
		};
	}, [y]);
	let M = t(async () => {
		if (!(A.current || w)) {
			A.current = !0, D(!0);
			try {
				let e = await S(l);
				C(e), T(!0), k(!1);
			} catch {
				k(!0);
			} finally {
				A.current = !1, D(!1);
			}
		}
	}, [l, w]);
	function N() {
		b(!0), M();
	}
	function P(e) {
		let t = u.toValue(e, x);
		s.includes(t) || c([...s, t]), g(""), b(!1);
	}
	function ee(e) {
		c(s.filter((t) => t !== e));
	}
	let F = I(m, me(x, f ? [...s, ...f] : s, u.valueOf));
	return /* @__PURE__ */ v(H, {
		label: e,
		helper: i,
		children: [
			s.length > 0 ? /* @__PURE__ */ _("div", {
				className: "mb-1 flex flex-wrap gap-1",
				children: s.map((e) => /* @__PURE__ */ v("span", {
					className: w && !u.isResolved(e, x) ? `${Ve} ${He}` : Ve,
					children: [/* @__PURE__ */ _("span", { children: u.toLabel(e, x) }), /* @__PURE__ */ _("button", {
						type: "button",
						"aria-label": `Remove ${u.toLabel(e, x)}`,
						onClick: () => {
							ee(e);
						},
						className: "text-muted hover:text-foreground",
						children: /* @__PURE__ */ _(h, { className: "h-3 w-3" })
					})]
				}, String(e)))
			}) : null,
			/* @__PURE__ */ v("div", {
				className: "relative",
				ref: j,
				children: [/* @__PURE__ */ _("input", {
					id: p,
					type: "text",
					value: m,
					placeholder: d,
					className: ze,
					onFocus: N,
					onChange: (e) => {
						g(e.target.value), b(!0);
					},
					onKeyDown: (e) => {
						e.key === "Enter" ? (e.preventDefault(), m.trim() !== "" && F.length > 0 && P(F[0])) : e.key === "Escape" && b(!1);
					}
				}), y && !O ? /* @__PURE__ */ _("div", {
					className: "mt-1 flex max-h-48 flex-col gap-0.5 overflow-auto rounded-xl border border-border bg-card p-1",
					children: E ? /* @__PURE__ */ v("span", {
						className: "flex items-center gap-2 px-2 py-1 text-sm text-muted",
						children: [/* @__PURE__ */ _(X, {}), "Loading…"]
					}) : F.length === 0 ? /* @__PURE__ */ _("span", {
						className: "px-2 py-1 text-sm text-muted",
						children: "No matches"
					}) : F.map((e) => /* @__PURE__ */ _("button", {
						type: "button",
						className: Be,
						onClick: () => {
							P(e);
						},
						children: e.name
					}, e.id))
				}) : null]
			}),
			O ? /* @__PURE__ */ _("span", {
				className: "mt-1 block",
				children: /* @__PURE__ */ _(Y, {
					kind: "error",
					children: "Could not load the list — existing values stay editable."
				})
			}) : null
		]
	});
}
var We = {
	toValue: (e) => e.id,
	valueOf: (e) => e.id,
	toLabel: (e, t) => ge(e, t),
	isResolved: (e, t) => _e(e, t)
}, Ge = {
	toValue: (e, t) => ve(e.name, t),
	valueOf: (e) => e.name,
	toLabel: (e) => e,
	isResolved: (e, t) => t.some((t) => t.name.toLowerCase() === e.toLowerCase())
}, Ke = {
	toValue: (e, t) => ve(e.name, t),
	valueOf: (e) => e.name,
	toLabel: (e) => e,
	isResolved: (e, t) => t.some((t) => t.name.toLowerCase() === e.toLowerCase())
};
function qe({ label: e, helper: t, values: n, onChange: r, placeholder: i, excludeValues: a }) {
	return /* @__PURE__ */ _(Ue, {
		label: e,
		helper: t,
		values: n,
		onChange: r,
		endpointPath: Z,
		adapter: We,
		placeholder: i,
		excludeValues: a
	});
}
function Je({ label: e, helper: t, values: n, onChange: r, placeholder: i, excludeValues: a }) {
	return /* @__PURE__ */ _(Ue, {
		label: e,
		helper: t,
		values: n,
		onChange: r,
		endpointPath: Q,
		adapter: Ge,
		placeholder: i,
		excludeValues: a
	});
}
function Ye({ label: e, helper: t, values: n, onChange: r, placeholder: i, excludeValues: a }) {
	return /* @__PURE__ */ _(Ue, {
		label: e,
		helper: t,
		values: n,
		onChange: r,
		endpointPath: Re,
		adapter: Ke,
		placeholder: i,
		excludeValues: a
	});
}
//#endregion
//#region src/studioMapLogic.ts
function Xe(e) {
	let t = {};
	for (let [n, r] of Object.entries(e)) t[n] = r;
	return t;
}
function Ze(e) {
	let t = {};
	for (let [n, r] of Object.entries(e)) {
		let e = Number(n);
		Number.isInteger(e) && typeof r == "string" && (t[e] = r);
	}
	return t;
}
//#endregion
//#region src/studioMap.tsx
var Qe = "/extensions/com.alextomas955.renamer/list-studios";
function $e({ map: e, onChange: t }) {
	let [r, i] = o([]);
	return n(() => {
		let e = !0;
		return S(Qe).then((t) => {
			e && i(t);
		}).catch(() => {}), () => {
			e = !1;
		};
	}, []), /* @__PURE__ */ _(Ae, {
		map: Xe(e),
		onChange: (e) => {
			t(Ze(e));
		},
		renderKey: (e, t, n) => /* @__PURE__ */ _(et, {
			draftKey: e,
			setDraftKey: t,
			existingKeys: n
		}),
		renderValue: (e, t) => /* @__PURE__ */ v(g, { children: [/* @__PURE__ */ _(U, {
			value: e,
			onChange: t,
			placeholder: "Destination root"
		}), /* @__PURE__ */ _(Me, { value: e })] }),
		renderKeyLabel: (e) => ge(Number(e), r),
		addLabel: "Add studio rule"
	});
}
function et({ draftKey: e, setDraftKey: t, existingKeys: n }) {
	return /* @__PURE__ */ _(qe, {
		label: "",
		values: e === "" ? [] : [Number(e)],
		onChange: (e) => {
			let n = e.at(-1);
			t(n === void 0 ? "" : String(n));
		},
		placeholder: "Search studios…",
		excludeValues: n.map(Number)
	});
}
//#endregion
//#region src/TokenLegend.tsx
var tt = [
	{
		token: "$title",
		label: "Title",
		kind: "core",
		insert: "$title"
	},
	{
		token: "$studio",
		label: "Studio",
		kind: "optional",
		insert: "{ - $studio}"
	},
	{
		token: "$parentStudio",
		label: "Parent studio",
		kind: "optional",
		insert: "{ - $parentStudio}"
	},
	{
		token: "$studioCode",
		label: "Studio code",
		kind: "optional",
		insert: "{ - $studioCode}"
	},
	{
		token: "$director",
		label: "Director",
		kind: "optional",
		insert: "{ - $director}"
	},
	{
		token: "$bitrate",
		label: "Bitrate",
		kind: "optional",
		insert: "{ [$bitrate]}"
	},
	{
		token: "$date",
		label: "Date",
		kind: "optional",
		insert: "{ - $date}"
	},
	{
		token: "$year",
		label: "Year",
		kind: "optional",
		insert: "{ [$year]}"
	},
	{
		token: "$height",
		label: "Height",
		kind: "optional",
		insert: "{ [$height]}"
	},
	{
		token: "$width",
		label: "Width",
		kind: "optional",
		insert: "{ [$width]}"
	},
	{
		token: "$resolution",
		label: "Resolution (e.g. 1080p)",
		kind: "optional",
		insert: "{ [$resolution]}"
	},
	{
		token: "$videoCodec",
		label: "Video codec",
		kind: "optional",
		insert: "{ [$videoCodec]}"
	},
	{
		token: "$audioCodec",
		label: "Audio codec",
		kind: "optional",
		insert: "{ [$audioCodec]}"
	},
	{
		token: "$frameRate",
		label: "Frame rate",
		kind: "optional",
		insert: "{ [$frameRate]}"
	},
	{
		token: "$duration",
		label: "Duration",
		kind: "optional",
		insert: "{ [$duration]}"
	},
	{
		token: "$performers",
		label: "Performers",
		kind: "optional",
		insert: "{ - $performers}"
	},
	{
		token: "$tags",
		label: "Tags",
		kind: "optional",
		insert: "{ - $tags}"
	},
	{
		token: "$ext",
		label: "Extension",
		kind: "core",
		insert: "$ext"
	}
];
function nt(e) {
	return `Inserts wrapped in an optional group: ${e.insert} — disappears cleanly when empty.`;
}
function rt({ onInsert: e }) {
	return /* @__PURE__ */ v("div", { children: [/* @__PURE__ */ v("p", {
		className: "mb-1 text-xs text-muted",
		children: [
			"Click a token to insert it. ",
			/* @__PURE__ */ _("span", {
				className: "text-foreground",
				children: "Optional tokens"
			}),
			" (marked",
			" ",
			/* @__PURE__ */ _("span", {
				className: "font-mono",
				children: "{ }"
			}),
			") insert wrapped so they vanish — with their punctuation — when empty. ",
			/* @__PURE__ */ _("span", {
				className: "text-foreground",
				children: "Core tokens"
			}),
			" insert as-is."
		]
	}), /* @__PURE__ */ _("div", {
		className: "flex flex-wrap gap-1",
		children: tt.map((t) => /* @__PURE__ */ v(V, {
			selected: !1,
			mono: !0,
			title: t.kind === "optional" ? nt(t) : t.label,
			onClick: () => {
				e(t.insert);
			},
			children: [t.token, t.kind === "optional" ? /* @__PURE__ */ _("span", {
				className: "ml-1 text-muted",
				children: "{ }"
			}) : null]
		}, t.token))
	})] });
}
//#endregion
//#region src/PreviewCard.tsx
function it(e, t) {
	switch (e) {
		case "empty": return "⚠ This template produces an empty name for this sample.";
		case "sanitized": return "⚠ Adjusted: illegal characters were stripped or replaced.";
		case "length-reduced": return t.droppedFields.length > 0 ? `⚠ Shortened to fit the path limit — dropped: ${t.droppedFields.join(", ")}.` : "⚠ Shortened to fit the path limit.";
		case "gating-skip": return "⚠ Would be skipped: a required field is missing for this sample.";
		default: return null;
	}
}
function at({ result: e }) {
	return /* @__PURE__ */ v("div", {
		className: "rounded-xl border border-border bg-card p-4",
		children: [
			/* @__PURE__ */ v("div", {
				className: "mb-2 text-xs font-medium uppercase tracking-wide text-muted",
				children: ["Sample: ", e.sampleLabel]
			}),
			e.folder.length > 0 ? /* @__PURE__ */ v("div", {
				className: "mb-1 text-xs text-secondary",
				children: [e.folder.split("/").join(" / "), " /"]
			}) : null,
			/* @__PURE__ */ _("div", {
				className: "font-mono text-sm text-muted line-through",
				children: e.oldName
			}),
			/* @__PURE__ */ v("div", {
				className: "font-mono text-sm text-foreground",
				children: [/* @__PURE__ */ _("span", {
					className: "text-muted",
					children: "Renamed → "
				}), e.newName]
			}),
			e.flags.length > 0 ? /* @__PURE__ */ _("div", {
				className: "mt-2 space-y-1",
				children: e.flags.map((t) => {
					let n = it(t, e);
					return n ? /* @__PURE__ */ _("p", {
						className: "text-xs text-amber-400",
						children: n
					}, t) : null;
				})
			}) : null
		]
	});
}
//#endregion
//#region src/dialog.tsx
var ot = "a[href], button:not([disabled]), textarea, input, select, [tabindex]:not([tabindex=\"-1\"])";
function st({ titleId: e, describedById: r, pending: i = !1, onCancel: o, size: s = "lg", children: c }) {
	let l = a(null), u = t(() => {
		i || o();
	}, [i, o]);
	return n(() => {
		let e = document.activeElement;
		return (l.current?.querySelector(ot))?.focus(), () => e?.focus();
	}, []), n(() => {
		function e(e) {
			if (e.key === "Escape") {
				e.preventDefault(), u();
				return;
			}
			if (e.key !== "Tab") return;
			let t = l.current;
			if (!t) return;
			let n = Array.from(t.querySelectorAll(ot));
			if (n.length === 0) return;
			let r = n[0], i = n[n.length - 1], a = document.activeElement;
			e.shiftKey && a === r ? (e.preventDefault(), i.focus()) : !e.shiftKey && a === i && (e.preventDefault(), r.focus());
		}
		return document.addEventListener("keydown", e), () => {
			document.removeEventListener("keydown", e);
		};
	}, [u]), /* @__PURE__ */ v("div", {
		className: "fixed inset-0 z-50 flex items-center justify-center",
		children: [/* @__PURE__ */ _("div", {
			className: "fixed inset-0 bg-black/60",
			onClick: u,
			"aria-hidden": "true"
		}), /* @__PURE__ */ _("div", {
			ref: l,
			role: "dialog",
			"aria-modal": "true",
			"aria-labelledby": e,
			"aria-describedby": r,
			className: `relative ${s === "sm" ? "max-w-sm" : s === "xl" ? "max-w-5xl" : "max-w-2xl"} w-full mx-4 rounded-lg border border-border bg-surface p-6 shadow-xl`,
			children: c
		})]
	});
}
function ct({ children: e }) {
	return /* @__PURE__ */ _("div", {
		className: "rounded border border-red-700 bg-red-950/60 px-3 py-2 text-sm text-red-200",
		children: e
	});
}
//#endregion
//#region src/UndoSection.tsx
var lt = "com.alextomas955.renamer", ut = `/extensions/${lt}/last-batch`, dt = `/extensions/${lt}/undo`, ft = "rename-undo-confirm-title", pt = "rename-undo-confirm-message", mt = 621355968e5, ht = 1e4, gt = mt * ht;
function _t(e) {
	return (e - gt) / ht;
}
function vt(e, t = Date.now()) {
	let n = t - e, r = Math.round(n / 1e3);
	if (r < 45) return "just now";
	let i = Math.round(r / 60);
	if (i < 60) return `${i} minute${i === 1 ? "" : "s"} ago`;
	let a = Math.round(i / 60);
	if (a < 24) return `${a} hour${a === 1 ? "" : "s"} ago`;
	let o = Math.round(a / 24);
	return o === 1 ? "yesterday" : o <= 7 ? `${o} days ago` : new Date(e).toLocaleDateString();
}
function yt(e) {
	return e instanceof C ? `${e.status} ${e.body}` : String(e);
}
function bt({ refreshKey: e }) {
	let [r, i] = o(null), [a, s] = o(!0), [c, l] = o(null), [u, d] = o(!1), [f, p] = o(!1), [h, g] = o(null), y = t(async () => {
		s(!0), l(null);
		try {
			let e = await S(ut);
			i(e);
		} catch (e) {
			l(yt(e));
		} finally {
			s(!1);
		}
	}, []);
	n(() => {
		y();
	}, [y, e]);
	let b = !!r && r.hasBatch && !r.consumed, x = r?.count ?? 0, w = r ? _t(r.writtenAtUtcTicks) : 0;
	async function T() {
		p(!0), g(null);
		try {
			let e = await S(dt, { method: "POST" }), t = (e.failed?.length ?? 0) + (e.skipped?.length ?? 0);
			if (t === 0) g({
				kind: "success",
				text: `Undone — ${e.undone} file${e.undone === 1 ? "" : "s"} moved back to their original names.`
			});
			else if (e.undone > 0) {
				let n = e.failed?.[0]?.reason ?? e.skipped?.[0]?.reason ?? "unknown reason";
				g({
					kind: "error",
					text: `Undo finished with problems — ${t} file${t === 1 ? "" : "s"} couldn't be moved back (${n}). The rest were restored.`
				});
			} else {
				let t = e.failed?.[0]?.reason ?? e.skipped?.[0]?.reason ?? "unknown reason";
				g({
					kind: "error",
					text: `Couldn't undo — ${t}. Nothing was changed.`
				});
			}
		} catch (e) {
			if (e instanceof C) {
				g({
					kind: "error",
					text: `Couldn't undo — ${yt(e)}. Nothing was changed.`
				});
				return;
			}
			g({
				kind: "success",
				text: "Undone — your files were moved back to their original names."
			});
		} finally {
			p(!1), d(!1), y();
		}
	}
	return /* @__PURE__ */ v("div", {
		className: "rounded-xl border border-border bg-card p-4",
		children: [
			/* @__PURE__ */ _("h3", {
				className: "text-base font-semibold text-foreground",
				children: "Undo last rename"
			}),
			/* @__PURE__ */ _("p", {
				className: "mb-4 mt-1 text-sm text-secondary",
				children: "This moves every file in that batch back to its original name. It can't be undone again. Undo history is kept in this extension's stored data, so it's lost if that data is cleared."
			}),
			a ? /* @__PURE__ */ v("div", {
				className: "flex items-center gap-2 text-sm text-secondary",
				children: [/* @__PURE__ */ _(X, {}), "Checking for a recent rename…"]
			}) : c ? /* @__PURE__ */ v("div", {
				className: "space-y-2",
				children: [/* @__PURE__ */ v(Y, {
					kind: "error",
					children: [
						"Couldn't check for a recent rename — ",
						c,
						"."
					]
				}), /* @__PURE__ */ _("div", { children: /* @__PURE__ */ _(J, {
					variant: "ghost",
					onClick: () => void y(),
					children: "Retry"
				}) })]
			}) : b ? /* @__PURE__ */ v("div", {
				className: "space-y-3",
				children: [/* @__PURE__ */ v("div", {
					className: "flex items-center justify-between gap-3",
					children: [/* @__PURE__ */ v("span", {
						className: "text-sm text-foreground",
						children: [
							"Last rename: ",
							x,
							" item",
							x === 1 ? "" : "s",
							" renamed · ",
							vt(w)
						]
					}), /* @__PURE__ */ v(J, {
						variant: "ghost",
						onClick: () => {
							d(!0);
						},
						disabled: f,
						children: [/* @__PURE__ */ _(m, { className: "h-4 w-4" }), "Undo last rename"]
					})]
				}), h ? /* @__PURE__ */ _(Y, {
					kind: h.kind,
					children: h.text
				}) : null]
			}) : /* @__PURE__ */ v("div", {
				className: "space-y-2",
				children: [/* @__PURE__ */ _("span", {
					className: "text-sm text-secondary",
					children: "No rename to undo."
				}), h ? /* @__PURE__ */ _("div", { children: /* @__PURE__ */ _(Y, {
					kind: h.kind,
					children: h.text
				}) }) : null]
			}),
			u ? /* @__PURE__ */ v(st, {
				titleId: ft,
				describedById: pt,
				pending: f,
				onCancel: () => {
					d(!1);
				},
				size: "sm",
				children: [
					/* @__PURE__ */ _("h2", {
						id: ft,
						className: "mb-2 text-lg font-semibold text-foreground",
						children: "Undo last rename?"
					}),
					/* @__PURE__ */ v("p", {
						id: pt,
						className: "mb-6 text-sm text-secondary",
						children: [
							"This moves ",
							x,
							" file",
							x === 1 ? "" : "s",
							" back to their original names. This can't be undone again."
						]
					}),
					/* @__PURE__ */ v("div", {
						className: "flex justify-end gap-3",
						children: [/* @__PURE__ */ _("button", {
							type: "button",
							onClick: () => {
								d(!1);
							},
							disabled: f,
							className: "px-4 py-2 text-sm text-secondary hover:text-foreground disabled:opacity-60",
							children: "Cancel"
						}), /* @__PURE__ */ v("button", {
							type: "button",
							onClick: () => void T(),
							disabled: f,
							className: "inline-flex items-center gap-2 rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-500 disabled:opacity-60",
							children: [
								f ? /* @__PURE__ */ _(X, {}) : null,
								"Undo ",
								x,
								" rename",
								x === 1 ? "" : "s"
							]
						})]
					})
				]
			}) : null
		]
	});
}
//#endregion
//#region node_modules/@tanstack/virtual-core/dist/esm/utils.js
function $(e, t, n) {
	let r = n.initialDeps ?? [], i, a = !0;
	function o() {
		let o;
		n.key && n.debug?.call(n) && (o = Date.now());
		let s = e();
		if (!(s.length !== r.length || s.some((e, t) => r[t] !== e))) return i;
		r = s;
		let c;
		if (n.key && n.debug?.call(n) && (c = Date.now()), i = t(...s), n.key && n.debug?.call(n)) {
			let e = Math.round((Date.now() - o) * 100) / 100, t = Math.round((Date.now() - c) * 100) / 100, r = t / 16, i = (e, t) => {
				for (e = String(e); e.length < t;) e = " " + e;
				return e;
			};
			console.info(`%c⏱ ${i(t, 5)} /${i(e, 5)} ms`, `
            font-size: .6rem;
            font-weight: bold;
            color: hsl(${Math.max(0, Math.min(120 - 120 * r, 120))}deg 100% 31%);`, n?.key);
		}
		return n?.onChange && !(a && n.skipInitialOnChange) && n.onChange(i), a = !1, i;
	}
	return o.updateDeps = (e) => {
		r = e;
	}, o;
}
function xt(e, t) {
	if (e === void 0) throw Error(`Unexpected undefined${t ? `: ${t}` : ""}`);
	return e;
}
var St = (e, t) => Math.abs(e - t) < 1.01, Ct = (e, t, n) => {
	let r;
	return function(...i) {
		e.clearTimeout(r), r = e.setTimeout(() => t.apply(this, i), n);
	};
}, wt = (e) => {
	let { offsetWidth: t, offsetHeight: n } = e;
	return {
		width: t,
		height: n
	};
}, Tt = (e) => e, Et = (e) => {
	let t = Math.max(e.startIndex - e.overscan, 0), n = Math.min(e.endIndex + e.overscan, e.count - 1), r = [];
	for (let e = t; e <= n; e++) r.push(e);
	return r;
}, Dt = (e, t) => {
	let n = e.scrollElement;
	if (!n) return;
	let r = e.targetWindow;
	if (!r) return;
	let i = (e) => {
		let { width: n, height: r } = e;
		t({
			width: Math.round(n),
			height: Math.round(r)
		});
	};
	if (i(wt(n)), !r.ResizeObserver) return () => {};
	let a = new r.ResizeObserver((t) => {
		let r = () => {
			let e = t[0];
			if (e?.borderBoxSize) {
				let t = e.borderBoxSize[0];
				if (t) {
					i({
						width: t.inlineSize,
						height: t.blockSize
					});
					return;
				}
			}
			i(wt(n));
		};
		e.options.useAnimationFrameWithResizeObserver ? requestAnimationFrame(r) : r();
	});
	return a.observe(n, { box: "border-box" }), () => {
		a.unobserve(n);
	};
}, Ot = { passive: !0 }, kt = typeof window > "u" ? !0 : "onscrollend" in window, At = (e, t) => {
	let n = e.scrollElement;
	if (!n) return;
	let r = e.targetWindow;
	if (!r) return;
	let i = 0, a = e.options.useScrollendEvent && kt ? () => void 0 : Ct(r, () => {
		t(i, !1);
	}, e.options.isScrollingResetDelay), o = (r) => () => {
		let { horizontal: o, isRtl: s } = e.options;
		i = o ? n.scrollLeft * (s && -1 || 1) : n.scrollTop, a(), t(i, r);
	}, s = o(!0), c = o(!1);
	n.addEventListener("scroll", s, Ot);
	let l = e.options.useScrollendEvent && kt;
	return l && n.addEventListener("scrollend", c, Ot), () => {
		n.removeEventListener("scroll", s), l && n.removeEventListener("scrollend", c);
	};
}, jt = (e, t, n) => {
	if (t?.borderBoxSize) {
		let e = t.borderBoxSize[0];
		if (e) return Math.round(e[n.options.horizontal ? "inlineSize" : "blockSize"]);
	}
	return e[n.options.horizontal ? "offsetWidth" : "offsetHeight"];
}, Mt = (e, { adjustments: t = 0, behavior: n }, r) => {
	var i, a;
	let o = e + t;
	(a = (i = r.scrollElement)?.scrollTo) == null || a.call(i, {
		[r.options.horizontal ? "left" : "top"]: o,
		behavior: n
	});
}, Nt = class {
	constructor(e) {
		this.unsubs = [], this.scrollElement = null, this.targetWindow = null, this.isScrolling = !1, this.scrollState = null, this.measurementsCache = [], this.itemSizeCache = /* @__PURE__ */ new Map(), this.laneAssignments = /* @__PURE__ */ new Map(), this.pendingMeasuredCacheIndexes = [], this.prevLanes = void 0, this.lanesChangedFlag = !1, this.lanesSettling = !1, this.scrollRect = null, this.scrollOffset = null, this.scrollDirection = null, this.scrollAdjustments = 0, this.elementsCache = /* @__PURE__ */ new Map(), this.now = () => {
			var e;
			return ((e = this.targetWindow?.performance)?.now)?.call(e) ?? Date.now();
		}, this.observer = /* @__PURE__ */ (() => {
			let e = null, t = () => e || (!this.targetWindow || !this.targetWindow.ResizeObserver ? null : e = new this.targetWindow.ResizeObserver((e) => {
				e.forEach((e) => {
					let t = () => {
						let t = e.target, n = this.indexFromElement(t);
						if (!t.isConnected) {
							this.observer.unobserve(t);
							return;
						}
						this.shouldMeasureDuringScroll(n) && this.resizeItem(n, this.options.measureElement(t, e, this));
					};
					this.options.useAnimationFrameWithResizeObserver ? requestAnimationFrame(t) : t();
				});
			}));
			return {
				disconnect: () => {
					var n;
					(n = t()) == null || n.disconnect(), e = null;
				},
				observe: (e) => t()?.observe(e, { box: "border-box" }),
				unobserve: (e) => t()?.unobserve(e)
			};
		})(), this.range = null, this.setOptions = (e) => {
			Object.entries(e).forEach(([t, n]) => {
				n === void 0 && delete e[t];
			}), this.options = {
				debug: !1,
				initialOffset: 0,
				overscan: 1,
				paddingStart: 0,
				paddingEnd: 0,
				scrollPaddingStart: 0,
				scrollPaddingEnd: 0,
				horizontal: !1,
				getItemKey: Tt,
				rangeExtractor: Et,
				onChange: () => {},
				measureElement: jt,
				initialRect: {
					width: 0,
					height: 0
				},
				scrollMargin: 0,
				gap: 0,
				indexAttribute: "data-index",
				initialMeasurementsCache: [],
				lanes: 1,
				isScrollingResetDelay: 150,
				enabled: !0,
				isRtl: !1,
				useScrollendEvent: !1,
				useAnimationFrameWithResizeObserver: !1,
				laneAssignmentMode: "estimate",
				...e
			};
		}, this.notify = (e) => {
			var t, n;
			(n = (t = this.options).onChange) == null || n.call(t, this, e);
		}, this.maybeNotify = $(() => (this.calculateRange(), [
			this.isScrolling,
			this.range ? this.range.startIndex : null,
			this.range ? this.range.endIndex : null
		]), (e) => {
			this.notify(e);
		}, {
			key: !1,
			debug: () => this.options.debug,
			initialDeps: [
				this.isScrolling,
				this.range ? this.range.startIndex : null,
				this.range ? this.range.endIndex : null
			]
		}), this.cleanup = () => {
			this.unsubs.filter(Boolean).forEach((e) => e()), this.unsubs = [], this.observer.disconnect(), this.rafId != null && this.targetWindow && (this.targetWindow.cancelAnimationFrame(this.rafId), this.rafId = null), this.scrollState = null, this.scrollElement = null, this.targetWindow = null;
		}, this._didMount = () => () => {
			this.cleanup();
		}, this._willUpdate = () => {
			let e = this.options.enabled ? this.options.getScrollElement() : null;
			if (this.scrollElement !== e) {
				if (this.cleanup(), !e) {
					this.maybeNotify();
					return;
				}
				this.scrollElement = e, this.scrollElement && "ownerDocument" in this.scrollElement ? this.targetWindow = this.scrollElement.ownerDocument.defaultView : this.targetWindow = this.scrollElement?.window ?? null, this.elementsCache.forEach((e) => {
					this.observer.observe(e);
				}), this.unsubs.push(this.options.observeElementRect(this, (e) => {
					this.scrollRect = e, this.maybeNotify();
				})), this.unsubs.push(this.options.observeElementOffset(this, (e, t) => {
					this.scrollAdjustments = 0, this.scrollDirection = t ? this.getScrollOffset() < e ? "forward" : "backward" : null, this.scrollOffset = e, this.isScrolling = t, this.scrollState && this.scheduleScrollReconcile(), this.maybeNotify();
				})), this._scrollToOffset(this.getScrollOffset(), {
					adjustments: void 0,
					behavior: void 0
				});
			}
		}, this.rafId = null, this.getSize = () => this.options.enabled ? (this.scrollRect = this.scrollRect ?? this.options.initialRect, this.scrollRect[this.options.horizontal ? "width" : "height"]) : (this.scrollRect = null, 0), this.getScrollOffset = () => this.options.enabled ? (this.scrollOffset = this.scrollOffset ?? (typeof this.options.initialOffset == "function" ? this.options.initialOffset() : this.options.initialOffset), this.scrollOffset) : (this.scrollOffset = null, 0), this.getFurthestMeasurement = (e, t) => {
			let n = /* @__PURE__ */ new Map(), r = /* @__PURE__ */ new Map();
			for (let i = t - 1; i >= 0; i--) {
				let t = e[i];
				if (n.has(t.lane)) continue;
				let a = r.get(t.lane);
				if (a == null || t.end > a.end ? r.set(t.lane, t) : t.end < a.end && n.set(t.lane, !0), n.size === this.options.lanes) break;
			}
			return r.size === this.options.lanes ? Array.from(r.values()).sort((e, t) => e.end === t.end ? e.index - t.index : e.end - t.end)[0] : void 0;
		}, this.getMeasurementOptions = $(() => [
			this.options.count,
			this.options.paddingStart,
			this.options.scrollMargin,
			this.options.getItemKey,
			this.options.enabled,
			this.options.lanes,
			this.options.laneAssignmentMode
		], (e, t, n, r, i, a, o) => (this.prevLanes !== void 0 && this.prevLanes !== a && (this.lanesChangedFlag = !0), this.prevLanes = a, this.pendingMeasuredCacheIndexes = [], {
			count: e,
			paddingStart: t,
			scrollMargin: n,
			getItemKey: r,
			enabled: i,
			lanes: a,
			laneAssignmentMode: o
		}), { key: !1 }), this.getMeasurements = $(() => [this.getMeasurementOptions(), this.itemSizeCache], ({ count: e, paddingStart: t, scrollMargin: n, getItemKey: r, enabled: i, lanes: a, laneAssignmentMode: o }, s) => {
			if (!i) return this.measurementsCache = [], this.itemSizeCache.clear(), this.laneAssignments.clear(), [];
			if (this.laneAssignments.size > e) for (let t of this.laneAssignments.keys()) t >= e && this.laneAssignments.delete(t);
			this.lanesChangedFlag && (this.lanesChangedFlag = !1, this.lanesSettling = !0, this.measurementsCache = [], this.itemSizeCache.clear(), this.laneAssignments.clear(), this.pendingMeasuredCacheIndexes = []), this.measurementsCache.length === 0 && !this.lanesSettling && (this.measurementsCache = this.options.initialMeasurementsCache, this.measurementsCache.forEach((e) => {
				this.itemSizeCache.set(e.key, e.size);
			}));
			let c = this.lanesSettling ? 0 : this.pendingMeasuredCacheIndexes.length > 0 ? Math.min(...this.pendingMeasuredCacheIndexes) : 0;
			this.pendingMeasuredCacheIndexes = [], this.lanesSettling && this.measurementsCache.length === e && (this.lanesSettling = !1);
			let l = this.measurementsCache.slice(0, c), u = Array(a).fill(void 0);
			for (let e = 0; e < c; e++) {
				let t = l[e];
				t && (u[t.lane] = e);
			}
			for (let i = c; i < e; i++) {
				let e = r(i), a = this.laneAssignments.get(i), c, d, f = o === "estimate" || s.has(e);
				if (a !== void 0 && this.options.lanes > 1) {
					c = a;
					let e = u[c], r = e === void 0 ? void 0 : l[e];
					d = r ? r.end + this.options.gap : t + n;
				} else {
					let e = this.options.lanes === 1 ? l[i - 1] : this.getFurthestMeasurement(l, i);
					d = e ? e.end + this.options.gap : t + n, c = e ? e.lane : i % this.options.lanes, this.options.lanes > 1 && f && this.laneAssignments.set(i, c);
				}
				let p = s.get(e), m = typeof p == "number" ? p : this.options.estimateSize(i), h = d + m;
				l[i] = {
					index: i,
					start: d,
					size: m,
					end: h,
					key: e,
					lane: c
				}, u[c] = i;
			}
			return this.measurementsCache = l, l;
		}, {
			key: !1,
			debug: () => this.options.debug
		}), this.calculateRange = $(() => [
			this.getMeasurements(),
			this.getSize(),
			this.getScrollOffset(),
			this.options.lanes
		], (e, t, n, r) => this.range = e.length > 0 && t > 0 ? Ft({
			measurements: e,
			outerSize: t,
			scrollOffset: n,
			lanes: r
		}) : null, {
			key: !1,
			debug: () => this.options.debug
		}), this.getVirtualIndexes = $(() => {
			let e = null, t = null, n = this.calculateRange();
			return n && (e = n.startIndex, t = n.endIndex), this.maybeNotify.updateDeps([
				this.isScrolling,
				e,
				t
			]), [
				this.options.rangeExtractor,
				this.options.overscan,
				this.options.count,
				e,
				t
			];
		}, (e, t, n, r, i) => r === null || i === null ? [] : e({
			startIndex: r,
			endIndex: i,
			overscan: t,
			count: n
		}), {
			key: !1,
			debug: () => this.options.debug
		}), this.indexFromElement = (e) => {
			let t = this.options.indexAttribute, n = e.getAttribute(t);
			return n ? parseInt(n, 10) : (console.warn(`Missing attribute name '${t}={index}' on measured element.`), -1);
		}, this.shouldMeasureDuringScroll = (e) => {
			if (!this.scrollState || this.scrollState.behavior !== "smooth") return !0;
			let t = this.scrollState.index ?? this.getVirtualItemForOffset(this.scrollState.lastTargetOffset)?.index;
			if (t !== void 0 && this.range) {
				let n = Math.max(this.options.overscan, Math.ceil((this.range.endIndex - this.range.startIndex) / 2)), r = Math.max(0, t - n), i = Math.min(this.options.count - 1, t + n);
				return e >= r && e <= i;
			}
			return !0;
		}, this.measureElement = (e) => {
			if (!e) {
				this.elementsCache.forEach((e, t) => {
					e.isConnected || (this.observer.unobserve(e), this.elementsCache.delete(t));
				});
				return;
			}
			let t = this.indexFromElement(e), n = this.options.getItemKey(t), r = this.elementsCache.get(n);
			r !== e && (r && this.observer.unobserve(r), this.observer.observe(e), this.elementsCache.set(n, e)), (!this.isScrolling || this.scrollState) && this.shouldMeasureDuringScroll(t) && this.resizeItem(t, this.options.measureElement(e, void 0, this));
		}, this.resizeItem = (e, t) => {
			let n = this.measurementsCache[e];
			if (!n) return;
			let r = t - (this.itemSizeCache.get(n.key) ?? n.size);
			r !== 0 && (this.scrollState?.behavior !== "smooth" && (this.shouldAdjustScrollPositionOnItemSizeChange === void 0 ? n.start < this.getScrollOffset() + this.scrollAdjustments : this.shouldAdjustScrollPositionOnItemSizeChange(n, r, this)) && this._scrollToOffset(this.getScrollOffset(), {
				adjustments: this.scrollAdjustments += r,
				behavior: void 0
			}), this.pendingMeasuredCacheIndexes.push(n.index), this.itemSizeCache = new Map(this.itemSizeCache.set(n.key, t)), this.notify(!1));
		}, this.getVirtualItems = $(() => [this.getVirtualIndexes(), this.getMeasurements()], (e, t) => {
			let n = [];
			for (let r = 0, i = e.length; r < i; r++) {
				let i = t[e[r]];
				n.push(i);
			}
			return n;
		}, {
			key: !1,
			debug: () => this.options.debug
		}), this.getVirtualItemForOffset = (e) => {
			let t = this.getMeasurements();
			if (t.length !== 0) return xt(t[Pt(0, t.length - 1, (e) => xt(t[e]).start, e)]);
		}, this.getMaxScrollOffset = () => {
			if (!this.scrollElement) return 0;
			if ("scrollHeight" in this.scrollElement) return this.options.horizontal ? this.scrollElement.scrollWidth - this.scrollElement.clientWidth : this.scrollElement.scrollHeight - this.scrollElement.clientHeight;
			{
				let e = this.scrollElement.document.documentElement;
				return this.options.horizontal ? e.scrollWidth - this.scrollElement.innerWidth : e.scrollHeight - this.scrollElement.innerHeight;
			}
		}, this.getOffsetForAlignment = (e, t, n = 0) => {
			if (!this.scrollElement) return 0;
			let r = this.getSize(), i = this.getScrollOffset();
			t === "auto" && (t = e >= i + r ? "end" : "start"), t === "center" ? e += (n - r) / 2 : t === "end" && (e -= r);
			let a = this.getMaxScrollOffset();
			return Math.max(Math.min(a, e), 0);
		}, this.getOffsetForIndex = (e, t = "auto") => {
			e = Math.max(0, Math.min(e, this.options.count - 1));
			let n = this.getSize(), r = this.getScrollOffset(), i = this.measurementsCache[e];
			if (!i) return;
			if (t === "auto") if (i.end >= r + n - this.options.scrollPaddingEnd) t = "end";
			else if (i.start <= r + this.options.scrollPaddingStart) t = "start";
			else return [r, t];
			if (t === "end" && e === this.options.count - 1) return [this.getMaxScrollOffset(), t];
			let a = t === "end" ? i.end + this.options.scrollPaddingEnd : i.start - this.options.scrollPaddingStart;
			return [this.getOffsetForAlignment(a, t, i.size), t];
		}, this.scrollToOffset = (e, { align: t = "start", behavior: n = "auto" } = {}) => {
			let r = this.getOffsetForAlignment(e, t), i = this.now();
			this.scrollState = {
				index: null,
				align: t,
				behavior: n,
				startedAt: i,
				lastTargetOffset: r,
				stableFrames: 0
			}, this._scrollToOffset(r, {
				adjustments: void 0,
				behavior: n
			}), this.scheduleScrollReconcile();
		}, this.scrollToIndex = (e, { align: t = "auto", behavior: n = "auto" } = {}) => {
			e = Math.max(0, Math.min(e, this.options.count - 1));
			let r = this.getOffsetForIndex(e, t);
			if (!r) return;
			let [i, a] = r, o = this.now();
			this.scrollState = {
				index: e,
				align: a,
				behavior: n,
				startedAt: o,
				lastTargetOffset: i,
				stableFrames: 0
			}, this._scrollToOffset(i, {
				adjustments: void 0,
				behavior: n
			}), this.scheduleScrollReconcile();
		}, this.scrollBy = (e, { behavior: t = "auto" } = {}) => {
			let n = this.getScrollOffset() + e, r = this.now();
			this.scrollState = {
				index: null,
				align: "start",
				behavior: t,
				startedAt: r,
				lastTargetOffset: n,
				stableFrames: 0
			}, this._scrollToOffset(n, {
				adjustments: void 0,
				behavior: t
			}), this.scheduleScrollReconcile();
		}, this.getTotalSize = () => {
			let e = this.getMeasurements(), t;
			if (e.length === 0) t = this.options.paddingStart;
			else if (this.options.lanes === 1) t = e[e.length - 1]?.end ?? 0;
			else {
				let n = Array(this.options.lanes).fill(null), r = e.length - 1;
				for (; r >= 0 && n.some((e) => e === null);) {
					let t = e[r];
					n[t.lane] === null && (n[t.lane] = t.end), r--;
				}
				t = Math.max(...n.filter((e) => e !== null));
			}
			return Math.max(t - this.options.scrollMargin + this.options.paddingEnd, 0);
		}, this._scrollToOffset = (e, { adjustments: t, behavior: n }) => {
			this.options.scrollToFn(e, {
				behavior: n,
				adjustments: t
			}, this);
		}, this.measure = () => {
			this.itemSizeCache = /* @__PURE__ */ new Map(), this.laneAssignments = /* @__PURE__ */ new Map(), this.notify(!1);
		}, this.setOptions(e);
	}
	scheduleScrollReconcile() {
		if (!this.targetWindow) {
			this.scrollState = null;
			return;
		}
		this.rafId ??= this.targetWindow.requestAnimationFrame(() => {
			this.rafId = null, this.reconcileScroll();
		});
	}
	reconcileScroll() {
		if (!this.scrollState || !this.scrollElement) return;
		if (this.now() - this.scrollState.startedAt > 5e3) {
			this.scrollState = null;
			return;
		}
		let e = this.scrollState.index == null ? void 0 : this.getOffsetForIndex(this.scrollState.index, this.scrollState.align), t = e ? e[0] : this.scrollState.lastTargetOffset, n = t !== this.scrollState.lastTargetOffset;
		if (!n && St(t, this.getScrollOffset())) {
			if (this.scrollState.stableFrames++, this.scrollState.stableFrames >= 1) {
				this.scrollState = null;
				return;
			}
		} else this.scrollState.stableFrames = 0, n && (this.scrollState.lastTargetOffset = t, this.scrollState.behavior = "auto", this._scrollToOffset(t, {
			adjustments: void 0,
			behavior: "auto"
		}));
		this.scheduleScrollReconcile();
	}
}, Pt = (e, t, n, r) => {
	for (; e <= t;) {
		let i = (e + t) / 2 | 0, a = n(i);
		if (a < r) e = i + 1;
		else if (a > r) t = i - 1;
		else return i;
	}
	return e > 0 ? e - 1 : 0;
};
function Ft({ measurements: e, outerSize: t, scrollOffset: n, lanes: r }) {
	let i = e.length - 1, a = (t) => e[t].start;
	if (e.length <= r) return {
		startIndex: 0,
		endIndex: i
	};
	let o = Pt(0, i, a, n), s = o;
	if (r === 1) for (; s < i && e[s].end < n + t;) s++;
	else if (r > 1) {
		let a = Array(r).fill(0);
		for (; s < i && a.some((e) => e < n + t);) {
			let t = e[s];
			a[t.lane] = t.end, s++;
		}
		let c = Array(r).fill(n + t);
		for (; o >= 0 && c.some((e) => e >= n);) {
			let t = e[o];
			c[t.lane] = t.start, o--;
		}
		o = Math.max(0, o - o % r), s = Math.min(i, s + (r - 1 - s % r));
	}
	return {
		startIndex: o,
		endIndex: s
	};
}
//#endregion
//#region node_modules/@tanstack/react-virtual/dist/esm/index.js
var It = typeof document < "u" ? e.useLayoutEffect : e.useEffect;
function Lt({ useFlushSync: t = !0, ...n }) {
	let r = e.useReducer(() => ({}), {})[1], i = {
		...n,
		onChange: (e, i) => {
			var a;
			t && i ? y(r) : r(), (a = n.onChange) == null || a.call(n, e, i);
		}
	}, [a] = e.useState(() => new Nt(i));
	return a.setOptions(i), It(() => a._didMount(), []), It(() => a._willUpdate()), a;
}
function Rt(e) {
	return Lt({
		observeElementRect: Dt,
		observeElementOffset: At,
		scrollToFn: Mt,
		...e
	});
}
//#endregion
//#region src/WarningBadge.tsx
var zt = {
	amber: "border-amber-400/40 bg-amber-400/10 text-amber-400",
	gray: "border-border bg-card text-muted",
	red: "border-red-700/50 bg-red-950/40 text-red-400"
};
function Bt(e) {
	let t = [];
	switch (e.status) {
		case "NoOp":
			t.push({
				label: "No change needed",
				variant: "gray"
			});
			break;
		case "SkipGated":
			t.push({
				label: "Skipped — needs a required field",
				variant: "amber"
			});
			break;
		case "SkipCollision":
			t.push({
				label: "Skipped — name conflict",
				variant: "amber"
			});
			break;
		case "SkipLocked":
			t.push({
				label: "Skipped — file in use",
				variant: "amber"
			});
			break;
		case "Failed":
			t.push({
				label: "Failed — rolled back",
				variant: "red"
			});
			break;
		case "Renamer":
		case "Move":
			e.suffixed && t.push({
				label: "Numbered to avoid a clash",
				variant: "amber"
			}), e.sanitized && t.push({
				label: "Cleaned for the filesystem",
				variant: "amber"
			});
			break;
	}
	return t;
}
function Vt({ badge: e }) {
	let t = e.variant === "amber" || e.variant === "red";
	return /* @__PURE__ */ v("span", {
		className: `inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs font-medium ${zt[e.variant]}`,
		children: [t ? /* @__PURE__ */ _(s, { className: "h-3 w-3" }) : null, e.label]
	});
}
function Ht({ item: e }) {
	let t = Bt(e);
	return t.length === 0 ? null : /* @__PURE__ */ _("span", {
		className: "inline-flex flex-wrap gap-1",
		children: t.map((e) => /* @__PURE__ */ _(Vt, { badge: e }, e.label))
	});
}
//#endregion
//#region src/dryRunLogic.ts
var Ut = /* @__PURE__ */ new Set([
	"SkipGated",
	"SkipCollision",
	"SkipLocked",
	"SkipBlocked",
	"SkipNoSpace",
	"SkipExcluded",
	"Failed"
]);
function Wt(e) {
	let t = 0, n = 0;
	for (let r of e) r.status === "Renamer" || r.status === "Move" ? t++ : Ut.has(r.status) && n++;
	return {
		renamed: t,
		skipped: n,
		scanned: e.length
	};
}
function Gt(e) {
	return e.status === "Renamer" || e.status === "Move" ? "will-change" : e.status === "NoOp" ? "no-change" : "attention";
}
function Kt(e) {
	let t = 0, n = 0, r = 0;
	for (let i of e) {
		let e = Gt(i);
		e === "will-change" ? t++ : e === "attention" ? n++ : r++;
	}
	return {
		willChange: t,
		attention: n,
		noChange: r,
		scanned: e.length
	};
}
function qt(e, t) {
	let n = t === "all" ? e : e.filter((e) => Gt(e) === t);
	if (t !== "all") return n;
	let r = {
		"will-change": 0,
		attention: 1,
		"no-change": 2
	};
	return n.map((e, t) => ({
		it: e,
		i: t
	})).sort((e, t) => r[Gt(e.it)] - r[Gt(t.it)] || e.i - t.i).map((e) => e.it);
}
function Jt(e, t) {
	let n = t.trim().toLowerCase();
	return n === "" ? e : e.filter((e) => `${e.oldFullPath}\n${e.newFullPath}\n${e.newBasename}\n${e.targetFolderPath}`.toLowerCase().includes(n));
}
function Yt(e, t) {
	switch (t) {
		case "type": return e.kind.toLowerCase();
		case "current": return e.oldFullPath.toLowerCase();
		case "new": return (e.newBasename || e.newFullPath).toLowerCase();
		case "destination": return e.targetFolderPath.toLowerCase();
	}
}
function Xt(e, t, n) {
	if (t === null) return e;
	let r = n === "asc" ? 1 : -1;
	return e.map((e, t) => ({
		it: e,
		i: t
	})).sort((e, n) => {
		let i = Yt(e.it, t), a = Yt(n.it, t);
		return i < a ? -r : i > a ? r : e.i - n.i;
	}).map((e) => e.it);
}
//#endregion
//#region src/DryRunModal.tsx
var Zt = { gridTemplateColumns: "5rem minmax(0,1fr) minmax(0,1fr) minmax(0,1fr) auto" }, Qt = 37, $t = "com.alextomas955.renamer", en = `/extensions/${$t}/scan-library`, tn = `/extensions/${$t}/last-scan`, nn = "rename-dry-run-title", rn = "rename-dry-run-summary", an = 1e3;
function on(e) {
	return e instanceof C ? `${e.status} ${e.body}` : String(e);
}
function sn(e) {
	if (!e) return e;
	let t = Math.max(e.lastIndexOf("/"), e.lastIndexOf("\\"));
	return t >= 0 ? e.slice(t + 1) : e;
}
function cn(e) {
	if (!e) return e;
	let t = Math.max(e.lastIndexOf("/"), e.lastIndexOf("\\"));
	return t >= 0 ? e.slice(0, t) : "";
}
function ln({ label: e, column: t, active: n, direction: r, onSort: i }) {
	return /* @__PURE__ */ v("button", {
		type: "button",
		onClick: () => {
			i(t);
		},
		"aria-sort": n ? r === "asc" ? "ascending" : "descending" : "none",
		className: "flex w-full items-center gap-1 px-3 py-2 text-left text-xs font-medium uppercase tracking-wide text-muted hover:text-foreground",
		children: [e, n ? _(r === "asc" ? l : c, {
			className: "h-3 w-3",
			"aria-hidden": !0
		}) : null]
	});
}
function un(e, t) {
	n(() => {
		if (!e) return;
		let n = !1, r = setInterval(() => {
			S(`/jobs/${e}`).then((e) => {
				n || (e.status === "completed" || e.status === "failed" || e.status === "cancelled") && (clearInterval(r), t(e));
			}).catch(() => {});
		}, an);
		return () => {
			n = !0, clearInterval(r);
		};
	}, [e]);
}
function dn({ options: e, onClose: t, onRenameAll: r, renaming: s }) {
	let [c, l] = o(null), [u, d] = o(null), [f, m] = o(null), [h, y] = o("all"), [b, x] = o(""), [C, w] = o(null), [T, E] = o("asc"), D = a(!1);
	n(() => {
		D.current || (D.current = !0, S(en, {
			method: "POST",
			body: JSON.stringify({ Options: JSON.stringify(e) })
		}).then((e) => {
			l(e.jobId);
		}).catch((e) => {
			m(on(e));
		}));
	}, []), un(c, (e) => {
		if (e.status !== "completed") {
			m(e.error ?? "the scan job did not complete");
			return;
		}
		S(tn).then((e) => {
			d(e);
		}).catch((e) => {
			m(on(e));
		});
	});
	let O = u ? Kt(u) : null, k = i(() => u ? Xt(Jt(qt(u, h), b), C, T) : [], [
		u,
		h,
		b,
		C,
		T
	]), A = (e) => {
		C === e ? E((e) => e === "asc" ? "desc" : "asc") : (w(e), E("asc"));
	}, j = a(null), M = Rt({
		count: k.length,
		getScrollElement: () => j.current,
		estimateSize: () => Qt,
		overscan: 12
	});
	return /* @__PURE__ */ v(st, {
		titleId: nn,
		describedById: rn,
		pending: s,
		onCancel: t,
		size: "xl",
		children: [
			/* @__PURE__ */ _("h2", {
				id: nn,
				className: "mb-2 text-lg font-semibold text-foreground",
				children: "Dry run"
			}),
			f ? /* @__PURE__ */ _("div", {
				className: "mb-4",
				children: /* @__PURE__ */ v(ct, { children: [
					"Couldn't scan your library — ",
					f,
					". Close and try again."
				] })
			}) : u === null || O === null ? /* @__PURE__ */ v("div", {
				className: "flex items-center gap-2 py-8 text-sm text-secondary",
				children: [/* @__PURE__ */ _(X, {}), "Scanning your library…"]
			}) : /* @__PURE__ */ v(g, { children: [
				/* @__PURE__ */ v("p", {
					id: rn,
					className: "mb-3 text-sm text-secondary",
					children: [
						/* @__PURE__ */ _("span", {
							className: "text-foreground",
							children: O.willChange
						}),
						" will change ·",
						" ",
						O.attention,
						" need attention · ",
						O.noChange,
						" no change · ",
						O.scanned,
						" ",
						"scanned"
					]
				}),
				O.scanned === 0 ? /* @__PURE__ */ _("p", {
					className: "py-8 text-center text-sm text-secondary",
					children: "No items match your current settings — nothing to rename."
				}) : /* @__PURE__ */ v(g, { children: [/* @__PURE__ */ _("div", {
					className: "mb-4 flex flex-wrap gap-2",
					children: [
						{
							key: "all",
							label: "All",
							n: O.scanned
						},
						{
							key: "will-change",
							label: "Will change",
							n: O.willChange
						},
						{
							key: "attention",
							label: "Needs attention",
							n: O.attention
						},
						{
							key: "no-change",
							label: "No change",
							n: O.noChange
						}
					].map((e) => {
						let t = h === e.key, n = e.n === 0 && e.key !== "all";
						return /* @__PURE__ */ v("button", {
							type: "button",
							disabled: n,
							onClick: () => {
								y(e.key);
							},
							"aria-pressed": t,
							className: `rounded-lg border px-3 py-1 text-xs font-medium ${t ? "border-accent bg-accent/15 text-foreground" : "border-border bg-card text-secondary hover:text-foreground"} ${n ? "opacity-40" : ""}`,
							children: [
								e.label,
								" (",
								e.n,
								")"
							]
						}, e.key);
					})
				}), /* @__PURE__ */ v("div", {
					className: "mb-3 flex items-center gap-2 rounded-lg border border-border bg-card px-3 py-1.5",
					children: [
						/* @__PURE__ */ _(p, {
							className: "h-4 w-4 shrink-0 text-muted",
							"aria-hidden": !0
						}),
						/* @__PURE__ */ _("input", {
							type: "text",
							value: b,
							onChange: (e) => {
								x(e.target.value);
							},
							placeholder: "Search names or destination…",
							"aria-label": "Search the dry-run rows",
							className: "w-full bg-transparent text-sm text-foreground outline-none placeholder:text-muted"
						}),
						b ? /* @__PURE__ */ _("button", {
							type: "button",
							onClick: () => {
								x("");
							},
							className: "shrink-0 text-xs text-muted hover:text-foreground",
							children: "Clear"
						}) : null
					]
				})] }),
				O.scanned === 0 ? null : k.length === 0 ? /* @__PURE__ */ _("p", {
					className: "py-8 text-center text-sm text-secondary",
					children: b ? "No rows match your search." : "No items in this view."
				}) : /* @__PURE__ */ _(g, { children: /* @__PURE__ */ v("div", {
					className: "overflow-hidden rounded border border-border text-sm",
					children: [
						/* @__PURE__ */ v("div", {
							className: "grid items-center border-b border-border bg-card",
							style: Zt,
							children: [
								/* @__PURE__ */ _(ln, {
									label: "Type",
									column: "type",
									active: C === "type",
									direction: T,
									onSort: A
								}),
								/* @__PURE__ */ _(ln, {
									label: "Current name",
									column: "current",
									active: C === "current",
									direction: T,
									onSort: A
								}),
								/* @__PURE__ */ _(ln, {
									label: "New name",
									column: "new",
									active: C === "new",
									direction: T,
									onSort: A
								}),
								/* @__PURE__ */ _(ln, {
									label: "Destination",
									column: "destination",
									active: C === "destination",
									direction: T,
									onSort: A
								}),
								/* @__PURE__ */ _("span", { className: "px-3 py-2" })
							]
						}),
						/* @__PURE__ */ _("div", {
							ref: j,
							className: "h-96 overflow-y-auto",
							children: /* @__PURE__ */ _("div", {
								className: "relative w-full",
								style: { height: `${M.getTotalSize()}px` },
								children: M.getVirtualItems().map((e) => {
									let t = k[e.index], n = Gt(t), r = n === "will-change", i = sn(t.oldFullPath), a = t.newBasename || sn(t.newFullPath), o = cn(t.oldFullPath), s = r && a !== i, c = r && t.targetFolderPath !== o;
									return /* @__PURE__ */ v("div", {
										className: `absolute left-0 grid w-full items-center border-b border-border hover:bg-card ${r ? "" : "opacity-70"}`,
										style: {
											...Zt,
											height: `${e.size}px`,
											transform: `translateY(${e.start}px)`
										},
										children: [
											/* @__PURE__ */ _("span", {
												className: "px-3 py-2 text-sm text-secondary",
												children: t.kind
											}),
											/* @__PURE__ */ _("span", {
												className: "truncate px-3 py-2 font-mono text-sm text-muted",
												title: t.oldFullPath,
												children: i
											}),
											/* @__PURE__ */ _("span", {
												className: `truncate px-3 py-2 font-mono text-sm ${r ? "text-foreground" : "text-muted"}`,
												title: r ? t.newFullPath : void 0,
												children: r ? s ? a : "(name unchanged)" : n === "no-change" ? "— unchanged" : "— will be skipped"
											}),
											/* @__PURE__ */ _("span", {
												className: "truncate px-3 py-2 font-mono text-xs text-muted",
												title: t.targetFolderPath,
												children: c ? /* @__PURE__ */ v("span", {
													className: "text-foreground",
													children: ["→ ", t.targetFolderPath]
												}) : t.targetFolderPath
											}),
											/* @__PURE__ */ _("span", {
												className: "px-3 py-2",
												children: /* @__PURE__ */ _(Ht, { item: t })
											})
										]
									}, t.fileId);
								})
							})
						}),
						/* @__PURE__ */ v("div", {
							className: "border-t border-border bg-card px-3 py-2 text-xs text-muted",
							children: [
								"Showing ",
								k.length,
								k.length === O.scanned ? "" : ` of ${O.scanned}`,
								" row",
								k.length === 1 ? "" : "s"
							]
						})
					]
				}) })
			] }),
			/* @__PURE__ */ v("div", {
				className: "mt-6 flex justify-end gap-3",
				children: [/* @__PURE__ */ _(J, {
					variant: "ghost",
					onClick: t,
					disabled: s,
					children: "Close"
				}), /* @__PURE__ */ v(J, {
					onClick: () => {
						u && r(u);
					},
					disabled: s || !O || O.willChange === 0,
					children: [
						s ? /* @__PURE__ */ _(X, {}) : null,
						"Rename ",
						O?.willChange ?? 0,
						" files"
					]
				})]
			})
		]
	});
}
//#endregion
//#region src/templateValidation.ts
var fn = new Set(tt.map((e) => e.token.slice(1).toLowerCase())), pn = tt.map((e) => e.token.slice(1));
function mn(e) {
	let t = e.startsWith("$") ? e.slice(1) : e;
	return fn.has(t.toLowerCase());
}
function hn(e) {
	let t = 0;
	for (let n of e) if (n === "{") t++;
	else if (n === "}" && (t--, t < 0)) return !1;
	return t === 0;
}
function gn(e) {
	let t = [], n = /* @__PURE__ */ new Set();
	for (let r = 0; r < e.length; r++) {
		if (e[r] !== "$") continue;
		if (e[r + 1] === "$") {
			r++;
			continue;
		}
		let i = r + 1;
		for (; i < e.length && /[A-Za-z0-9_]/.test(e[i]);) i++;
		if (i === r + 1) continue;
		let a = e.slice(r + 1, i), o = a.toLowerCase();
		!fn.has(o) && !n.has(o) && (n.add(o), t.push(`$${a}`)), r = i - 1;
	}
	return t;
}
function _n(e, t) {
	for (let n = 0; n < e.length; n++) {
		if (e[n] !== "$") continue;
		if (e[n + 1] === "$") {
			n++;
			continue;
		}
		let r = n + 1;
		for (; r < e.length && /[A-Za-z0-9_]/.test(e[r]);) r++;
		if (r !== n + 1) {
			if (e.slice(n + 1, r).toLowerCase() === t) return !0;
			n = r - 1;
		}
	}
	return !1;
}
function vn(e, t, n) {
	let r = (e.startsWith("$") ? e.slice(1) : e).toLowerCase();
	return _n(t, r) || _n(n, r);
}
function yn(e, t) {
	let n = e.length, r = t.length, i = Array.from({ length: r + 1 }, (e, t) => t);
	for (let a = 1; a <= n; a++) {
		let n = i[0];
		i[0] = a;
		for (let o = 1; o <= r; o++) {
			let r = i[o];
			i[o] = e[a - 1] === t[o - 1] ? n : Math.min(n, i[o - 1], i[o]) + 1, n = r;
		}
	}
	return i[r];
}
function bn(e) {
	let t = (e.startsWith("$") ? e.slice(1) : e).toLowerCase(), n, r = Infinity;
	for (let e of fn) {
		let i = yn(t, e);
		i < r && (r = i, n = e);
	}
	return n !== void 0 && r > 0 && r <= 2 ? `$${n}` : void 0;
}
//#endregion
//#region src/presets.ts
var xn = [
	{
		label: "Date – Title [Resolution]",
		filenameTemplate: "{$date - }$title{ [$resolution]}"
	},
	{
		label: "Title + resolution",
		filenameTemplate: "$title{ [$resolution]}"
	},
	{
		label: "Studio – Title [Res]",
		filenameTemplate: "$studio{ - $title}{ [$resolution]}"
	},
	{
		label: "Date – Title",
		filenameTemplate: "$date{ - $title}"
	},
	{
		label: "Performers – Title",
		filenameTemplate: "$performers{ - $title}"
	}
], Sn = "com.alextomas955.renamer", Cn = "options", wn = `/extensions/${Sn}/data`, Tn = `/extensions/${Sn}/preview-sample`, En = `/extensions/${Sn}/renamer-library`, Dn = 250, On = 1e3;
function kn(e) {
	let t = e.trim();
	return t.startsWith(".") && (t = t.slice(1)), t.toLowerCase();
}
var An = [
	{
		value: "None",
		label: "None"
	},
	{
		value: "Lower",
		label: "lower case"
	},
	{
		value: "Title",
		label: "Title Case"
	}
], jn = [{
	value: "DropAll",
	label: "Drop all when over the max"
}, {
	value: "KeepFirst",
	label: "Keep the first N"
}], Mn = [
	{
		value: "NameAsc",
		label: "Name (A→Z)"
	},
	{
		value: "None",
		label: "Keep original order"
	},
	{
		value: "IdAsc",
		label: "By internal id"
	},
	{
		value: "FavoriteFirst",
		label: "Favorites first, then name"
	}
], Nn = [{
	value: "NameAsc",
	label: "Name (A→Z)"
}, {
	value: "None",
	label: "Keep original order"
}], Pn = [
	{
		value: "Male",
		label: "Male"
	},
	{
		value: "Female",
		label: "Female"
	},
	{
		value: "TransgenderMale",
		label: "Transgender male"
	},
	{
		value: "TransgenderFemale",
		label: "Transgender female"
	},
	{
		value: "Intersex",
		label: "Intersex"
	},
	{
		value: "NonBinary",
		label: "Non-binary"
	}
], Fn = [
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
].map((e) => ({
	value: e,
	label: e
})), In = [
	{
		value: "yyyy-MM-dd",
		example: "2026-03-12"
	},
	{
		value: "yyyy",
		example: "2026"
	},
	{
		value: "MM-dd-yyyy",
		example: "03-12-2026"
	},
	{
		value: "dd.MM.yyyy",
		example: "12.03.2026"
	},
	{
		value: "yyyy.MM.dd",
		example: "2026.03.12"
	}
], Ln = [
	{
		value: "hh\\-mm\\-ss",
		example: "01-23-45"
	},
	{
		value: "hh\\.mm\\.ss",
		example: "01.23.45"
	},
	{
		value: "mm\\-ss",
		example: "83-45"
	}
], Rn = [
	{
		value: ", ",
		label: "Comma + space ( , )"
	},
	{
		value: " · ",
		label: "Middot ( · )"
	},
	{
		value: " ",
		label: "Space ( ␣ )"
	},
	{
		value: " - ",
		label: "Dash ( - )"
	}
], zn = [
	{
		value: " ({n})",
		example: "name (1).mp4"
	},
	{
		value: "_{n}",
		example: "name_1.mp4"
	},
	{
		value: " - {n}",
		example: "name - 1.mp4"
	}
];
function Bn({ value: e, emptySamples: t = [] }) {
	let n = [];
	hn(e) || n.push("Unmatched { or } — it'll still render, but check your groups.");
	for (let t of gn(e)) {
		let e = bn(t);
		n.push(e ? `${t} isn't a known token — it'll render as empty. Did you mean ${e}?` : `${t} isn't a known token — it'll render as empty.`);
	}
	for (let e of t) n.push(`This template produces an empty name for the "${e}" sample.`);
	return n.length === 0 ? null : /* @__PURE__ */ _("div", {
		className: "mt-1 space-y-1",
		role: "status",
		"aria-live": "polite",
		children: n.map((e) => /* @__PURE__ */ v("p", {
			className: "flex items-start gap-1 text-xs text-amber-400",
			children: [/* @__PURE__ */ _(s, { className: "h-3 w-3 shrink-0" }), /* @__PURE__ */ _("span", { children: e })]
		}, e))
	});
}
function Vn({ values: e }) {
	let t = [];
	for (let n of e) {
		if (mn(n)) continue;
		let e = bn(n), r = e ? e.slice(1) : void 0;
		t.push(r ? `"${n}" isn't a known token — it'll be ignored. Did you mean ${r}?` : `"${n}" isn't a known token — it'll be ignored.`);
	}
	return t.length === 0 ? null : /* @__PURE__ */ _("div", {
		className: "mt-1 space-y-1",
		role: "status",
		"aria-live": "polite",
		children: t.map((e) => /* @__PURE__ */ v("p", {
			className: "flex items-start gap-1 text-xs text-amber-400",
			children: [/* @__PURE__ */ _(s, { className: "h-3 w-3 shrink-0" }), /* @__PURE__ */ _("span", { children: e })]
		}, e))
	});
}
function Hn({ onApply: e }) {
	return /* @__PURE__ */ v("div", { children: [
		/* @__PURE__ */ _("span", {
			className: "mb-1 block text-xs font-medium uppercase tracking-wide text-muted",
			children: "Presets"
		}),
		/* @__PURE__ */ _("div", {
			className: "flex flex-wrap gap-1",
			children: xn.map((t) => /* @__PURE__ */ _(V, {
				selected: !1,
				title: t.filenameTemplate,
				onClick: () => {
					e(t.filenameTemplate);
				},
				children: t.label
			}, t.label))
		}),
		/* @__PURE__ */ _("p", {
			className: "mt-1 text-xs text-muted",
			children: "Click a preset to fill the filename template. You can edit it afterwards."
		})
	] });
}
function Un({ dirty: e, saving: t, saveError: n, savedFlash: r, canSave: i, onSave: a, onDiscard: o }) {
	return e ? /* @__PURE__ */ _("div", {
		className: "pointer-events-none fixed inset-x-0 bottom-0 z-50 flex justify-center px-4 py-4",
		children: /* @__PURE__ */ v("div", {
			className: "pointer-events-auto flex w-full max-w-3xl items-center gap-4 rounded-2xl border border-border bg-card px-5 shadow-lg",
			style: {
				paddingTop: "0.875rem",
				paddingBottom: "0.875rem"
			},
			children: [
				/* @__PURE__ */ _("span", { className: `h-2 w-2 shrink-0 rounded-full ${n ? "bg-red-400" : r ? "bg-green-400" : "bg-amber-400"}` }),
				/* @__PURE__ */ _("div", {
					className: "min-w-0 flex-1",
					children: n ? /* @__PURE__ */ v(Y, {
						kind: "error",
						children: [
							"Couldn't save settings — ",
							n,
							". Your changes are still here; try Save again."
						]
					}) : r ? /* @__PURE__ */ _(Y, {
						kind: "success",
						children: "Settings saved."
					}) : /* @__PURE__ */ v(g, { children: [/* @__PURE__ */ _("div", {
						className: "text-sm font-semibold text-foreground",
						children: "Unsaved changes"
					}), /* @__PURE__ */ _("div", {
						className: "mt-0.5 text-xs text-secondary",
						children: "Nothing on disk changes until you save. Running a rename requires saving first."
					})] })
				}),
				/* @__PURE__ */ v("div", {
					className: "flex shrink-0 items-center gap-3",
					children: [/* @__PURE__ */ _(J, {
						variant: "ghost",
						onClick: o,
						disabled: t,
						children: "Discard"
					}), /* @__PURE__ */ v(J, {
						onClick: a,
						disabled: !i || t,
						children: [t ? /* @__PURE__ */ _(X, {}) : null, "Save changes"]
					})]
				})
			]
		})
	}) : null;
}
async function Wn(e, t) {
	let n = {
		...t,
		...e
	};
	try {
		await S(`${wn}/${Cn}`, {
			method: "PUT",
			body: JSON.stringify(JSON.stringify(n))
		});
	} catch (e) {
		if (e instanceof C) throw e;
	}
}
function Gn() {
	let e = T(Sn), [r, i] = o(() => D()), [c, l] = o(() => D()), [u, d] = o(!0), [f, p] = o(null), [m, h] = o(!1), [y, b] = o(null), [x, w] = o(!1), [E, O] = o(!1), [k, A] = o(!1), [j, M] = o(""), N = a({}), [P, ee] = o(null), [F, te] = o(!1), ne = a(null), re = a(null), ie = a("filename"), ae = JSON.stringify(r) !== JSON.stringify(c), oe = ae || k, [se, ue] = o(!1), [de, fe] = o(!1), [I, me] = o(null);
	function he(e) {
		return new Promise((t, n) => {
			let r = setInterval(() => {
				S(`/jobs/${e}`).then((e) => {
					e.status === "completed" ? (clearInterval(r), t()) : (e.status === "failed" || e.status === "cancelled") && (clearInterval(r), n(Error(e.error ?? "the job did not complete")));
				}).catch(() => {});
			}, On);
		});
	}
	let ge = t(async (e) => {
		fe(!0), me(null);
		try {
			let t = e;
			if (!t) {
				let { jobId: e } = await S(`/extensions/${Sn}/scan-library`, { method: "POST" });
				await he(e), t = await S(`/extensions/${Sn}/last-scan`);
			}
			let n = Wt(t), { jobId: r } = await S(En, { method: "POST" });
			await he(r), ue(!1), me({
				kind: "success",
				text: `Renamed ${n.renamed} file${n.renamed === 1 ? "" : "s"}` + (n.skipped > 0 ? `, ${n.skipped} skipped` : "") + "."
			});
		} catch (e) {
			let t = e instanceof C ? `${e.status} ${e.body}` : String(e);
			me({
				kind: "error",
				text: `Couldn't rename — ${t}. Nothing was changed; you can try again.`
			});
		} finally {
			fe(!1);
		}
	}, []), _e = t(async () => {
		d(!0), p(null), A(!1);
		try {
			let t = (await e.getAll())[Cn];
			if (t) {
				O(!1);
				let e;
				try {
					e = JSON.parse(t);
				} catch {
					N.current = {};
					let e = D();
					i(e), l(e), A(!0);
					return;
				}
				N.current = ce(e);
				let n = le(e), r = {
					...n,
					EnableStudioDestinations: n.EnableStudioDestinations || Object.keys(n.StudioDestinations).length > 0,
					EnableTagDestinations: n.EnableTagDestinations || Object.keys(n.TagDestinations).length > 0,
					EnableAdvancedRouting: n.EnableAdvancedRouting || n.AllowedRoots.length > 0 || n.PathDestinations.length > 0
				};
				i(r), l(r);
			} else {
				O(!0), N.current = {};
				let e = D();
				i(e), l(e);
			}
		} catch (e) {
			p(e instanceof C ? `${e.status} ${e.body}` : String(e));
		} finally {
			d(!1);
		}
	}, [e]);
	n(() => {
		_e();
	}, [_e]), n(() => {
		if (u) return;
		let e = setTimeout(() => {
			S(Tn, {
				method: "POST",
				body: JSON.stringify({ Options: r })
			}).then((e) => {
				ee(e), te(!1);
			}).catch(() => {
				te(!0);
			});
		}, Dn);
		return () => {
			clearTimeout(e);
		};
	}, [r, u]);
	async function ve() {
		h(!0), b(null);
		try {
			await Wn(r, N.current), l(r), O(!1), A(!1), w(!0), setTimeout(() => {
				w(!1);
			}, 3e3);
		} catch (e) {
			b(e instanceof C ? `${e.status} ${e.body}` : String(e));
		} finally {
			h(!1);
		}
	}
	function L(e, t) {
		i((n) => ({
			...n,
			[e]: t
		}));
	}
	function R(e, t) {
		i((n) => ({
			...n,
			[e]: {
				...n[e],
				...t
			}
		}));
	}
	function z(e) {
		let t = ie.current, n = t === "folder" ? re.current : ne.current, i = t === "folder" ? "FolderTemplate" : "FilenameTemplate", a = r[i];
		if (n && typeof n.selectionStart == "number") {
			let t = n.selectionStart, r = n.selectionEnd ?? t;
			L(i, a.slice(0, t) + e + a.slice(r)), requestAnimationFrame(() => {
				n.focus();
				let r = t + e.length;
				n.setSelectionRange(r, r);
			});
		} else L(i, a + e);
	}
	if (u) return /* @__PURE__ */ v("div", {
		className: "flex items-center gap-2 text-sm text-secondary",
		children: [/* @__PURE__ */ _(X, {}), "Loading settings…"]
	});
	if (f) return /* @__PURE__ */ v("div", {
		className: "space-y-3",
		children: [/* @__PURE__ */ v(Y, {
			kind: "error",
			children: [
				"Couldn't load your saved settings — ",
				f,
				". Retry, or continue with defaults below."
			]
		}), /* @__PURE__ */ _("div", { children: /* @__PURE__ */ _(J, {
			variant: "ghost",
			onClick: () => void _e(),
			children: "Retry"
		}) })]
	});
	let B = (e) => r[e], ye = (P ?? []).filter((e) => e.flags.includes("empty")).map((e) => e.sampleLabel), be = vn("performers", r.FilenameTemplate, r.FolderTemplate), Le = vn("tags", r.FilenameTemplate, r.FolderTemplate), Z = vn("date", r.FilenameTemplate, r.FolderTemplate), Q = vn("duration", r.FilenameTemplate, r.FolderTemplate);
	return /* @__PURE__ */ v("div", {
		className: "space-y-6",
		style: ae ? { paddingBottom: "5rem" } : void 0,
		children: [
			/* @__PURE__ */ v("div", {
				className: "grid grid-cols-1 gap-6 lg:grid-cols-3",
				children: [/* @__PURE__ */ v("div", {
					className: "col-span-2",
					children: [k ? /* @__PURE__ */ _(Y, {
						kind: "error",
						children: "Your saved settings couldn't be read and have been reset to defaults. Review the options below and save to store a clean copy."
					}) : E ? /* @__PURE__ */ _(Y, {
						kind: "muted",
						children: "Using default settings — pick a preset or write a template, then save."
					}) : null, /* @__PURE__ */ v(q, {
						title: "Filename & destination",
						description: "Set how files are named and where they go — pick a preset or write your own template.",
						children: [
							/* @__PURE__ */ _(Hn, { onApply: (e) => {
								L("FilenameTemplate", e);
							} }),
							/* @__PURE__ */ _(H, {
								label: "Filename template",
								children: /* @__PURE__ */ _(U, {
									value: r.FilenameTemplate,
									onChange: (e) => {
										L("FilenameTemplate", e);
									},
									onFocus: () => ie.current = "filename",
									inputRef: ne,
									mono: !0,
									placeholder: "$title"
								})
							}),
							/* @__PURE__ */ _(Bn, {
								value: r.FilenameTemplate,
								emptySamples: ye
							}),
							/* @__PURE__ */ _(rt, { onInsert: z }),
							/* @__PURE__ */ v("div", {
								className: "border-t border-border pt-4",
								children: [
									/* @__PURE__ */ _("div", {
										className: "text-base font-semibold text-foreground",
										children: "Where files go"
									}),
									/* @__PURE__ */ _("p", {
										className: "mb-4 mt-1 text-sm text-secondary",
										children: "Folder path template — moves files on rename."
									}),
									/* @__PURE__ */ _(H, {
										label: "Folder template",
										helper: "Blank = no folder move (rename in place). Use / for sub-folders, e.g. $studio / $year.",
										children: /* @__PURE__ */ _(U, {
											value: r.FolderTemplate,
											onChange: (e) => {
												L("FolderTemplate", e);
											},
											onFocus: () => ie.current = "folder",
											inputRef: re,
											mono: !0,
											placeholder: "$studio / $year"
										})
									}),
									/* @__PURE__ */ _(Bn, { value: r.FolderTemplate })
								]
							})
						]
					})]
				}), /* @__PURE__ */ _("div", { children: /* @__PURE__ */ v("div", {
					className: "space-y-4 rounded-2xl border border-border bg-surface p-5 shadow-sm lg:sticky lg:top-16",
					children: [
						/* @__PURE__ */ _("div", {
							className: "text-base font-semibold text-foreground",
							children: "Live preview"
						}),
						/* @__PURE__ */ _("p", {
							className: "mb-4 mt-1 text-sm text-secondary",
							children: "Old → new for sample items, before anything touches disk."
						}),
						F ? /* @__PURE__ */ _(Y, {
							kind: "error",
							children: "Preview unavailable — saved naming still works."
						}) : null,
						P == null ? /* @__PURE__ */ v("div", {
							className: "flex items-center gap-2 text-sm text-secondary",
							children: [/* @__PURE__ */ _(X, {}), "Rendering preview…"]
						}) : /* @__PURE__ */ _("div", {
							className: "space-y-3",
							children: P.map((e) => /* @__PURE__ */ _(at, { result: e }, e.sampleLabel))
						})
					]
				}) })]
			}),
			/* @__PURE__ */ _(Pe, {
				title: "What gets renamed",
				hint: "Scope and required fields"
			}),
			/* @__PURE__ */ v(q, { children: [
				/* @__PURE__ */ _(G, {
					label: "Only rename organized items",
					checked: r.OnlyOrganized,
					onChange: (e) => {
						L("OnlyOrganized", e);
					},
					helper: "Only rename items you've marked Organized — skips un-curated items so they don't get junk names."
				}),
				/* @__PURE__ */ _(G, {
					label: "Use filename as title when none is set",
					checked: r.FilenameAsTitle,
					onChange: (e) => {
						L("FilenameAsTitle", e);
					},
					helper: "When an item has no title, use its current filename (without extension) as the title."
				}),
				/* @__PURE__ */ v(H, {
					label: "Required fields",
					helper: "Items whose listed tokens resolve to nothing are skipped instead of renamed. Default: title.",
					children: [
						/* @__PURE__ */ _(Te, {
							values: r.RequiredFields,
							onChange: (e) => {
								L("RequiredFields", e);
							},
							placeholder: "Add token, press Enter"
						}),
						/* @__PURE__ */ _(Oe, {
							tokens: pn,
							values: r.RequiredFields,
							onAdd: (e) => {
								L("RequiredFields", r.RequiredFields.includes(e) ? r.RequiredFields : [...r.RequiredFields, e]);
							}
						}),
						/* @__PURE__ */ _(Vn, { values: r.RequiredFields })
					]
				})
			] }),
			/* @__PURE__ */ _(Pe, {
				title: "Run & automation",
				hint: "When renames happen"
			}),
			/* @__PURE__ */ v(q, { children: [/* @__PURE__ */ _(G, {
				label: "Auto-rename on update",
				checked: r.AutoRenamerOnUpdate,
				onChange: (e) => {
					L("AutoRenamerOnUpdate", e);
				},
				helper: "When on, renames a video or image automatically whenever its metadata changes — respects every gating and routing rule below. The core of a hands-off library. Off by default."
			}), /* @__PURE__ */ v("div", {
				className: "border-t border-border pt-4",
				children: [
					/* @__PURE__ */ v("div", {
						className: "flex flex-wrap items-start gap-4",
						children: [/* @__PURE__ */ v("div", {
							className: "min-w-0 flex-1",
							children: [/* @__PURE__ */ _("div", {
								className: "text-base font-semibold text-foreground",
								children: "Run for the whole library"
							}), /* @__PURE__ */ _("p", {
								className: "mt-1 text-sm text-secondary",
								children: "Apply your rules to every matching item now. Preview first with a dry run — it writes nothing."
							})]
						}), /* @__PURE__ */ v("div", {
							className: "flex shrink-0 flex-wrap items-center gap-3",
							children: [/* @__PURE__ */ _(J, {
								variant: "ghost",
								onClick: () => {
									ue(!0);
								},
								children: "Dry run"
							}), /* @__PURE__ */ v(J, {
								onClick: () => void ge(),
								disabled: ae || de,
								children: [de ? /* @__PURE__ */ _(X, {}) : null, "Rename all files"]
							})]
						})]
					}),
					ae ? /* @__PURE__ */ v("p", {
						className: "mt-2 flex items-start gap-1 text-xs text-amber-400",
						role: "status",
						"aria-live": "polite",
						children: [/* @__PURE__ */ _(s, { className: "h-3 w-3 shrink-0" }), /* @__PURE__ */ _("span", { children: "Dry run previews your edits before saving. Save before “Rename all files” to run them for real." })]
					}) : null,
					I ? /* @__PURE__ */ _("p", {
						className: "mt-2",
						children: /* @__PURE__ */ v(Y, {
							kind: I.kind,
							children: [
								I.kind === "success" ? "✓ " : "",
								I.text,
								I.kind === "success" ? /* @__PURE__ */ v(g, { children: [" ", /* @__PURE__ */ _("button", {
									type: "button",
									onClick: () => {
										document.getElementById("rename-undo-section")?.scrollIntoView({ behavior: "smooth" });
									},
									className: "text-accent",
									children: "Undo"
								})] }) : null
							]
						})
					}) : null
				]
			})] }),
			se ? /* @__PURE__ */ _(dn, {
				options: r,
				onClose: () => {
					ue(!1);
				},
				onRenameAll: (e) => void ge(e),
				renaming: de
			}) : null,
			/* @__PURE__ */ _(Pe, {
				title: "Token settings",
				hint: "Appear only for tokens you're using"
			}),
			/* @__PURE__ */ v("div", {
				className: "space-y-4",
				children: [
					be ? /* @__PURE__ */ v(q, {
						title: "Performers",
						badge: /* @__PURE__ */ _(Ne, {
							mono: !0,
							children: "$performers"
						}),
						accent: !0,
						children: [
							/* @__PURE__ */ _(H, {
								label: "Separator",
								children: /* @__PURE__ */ _(Ce, {
									value: B("Performers").Separator,
									onChange: (e) => {
										R("Performers", { Separator: e });
									},
									options: Rn,
									customPlaceholder: "Custom separator"
								})
							}),
							/* @__PURE__ */ _(H, {
								label: "Max count",
								helper: "0 = unlimited",
								children: /* @__PURE__ */ _(xe, {
									value: B("Performers").MaxCount,
									min: 0,
									onChange: (e) => {
										R("Performers", { MaxCount: e });
									}
								})
							}),
							/* @__PURE__ */ _(H, {
								label: "On overflow",
								children: /* @__PURE__ */ _(W, {
									value: B("Performers").OnOverflow,
									onChange: (e) => {
										R("Performers", { OnOverflow: e });
									},
									options: jn
								})
							}),
							/* @__PURE__ */ _(H, {
								label: "Sort",
								helper: "The id and favorite orders apply to performers only.",
								children: /* @__PURE__ */ _(W, {
									value: B("Performers").Sort,
									onChange: (e) => {
										R("Performers", { Sort: e });
									},
									options: Mn
								})
							}),
							/* @__PURE__ */ _(H, {
								label: "Ignore genders",
								helper: "Drop performers of these genders before the max-count limit. A performer with no gender is always kept. None selected = off.",
								children: /* @__PURE__ */ _(Ee, {
									options: Pn,
									values: B("Performers").IgnoreGenders,
									onChange: (e) => {
										R("Performers", { IgnoreGenders: e });
									}
								})
							}),
							/* @__PURE__ */ _(H, {
								label: "Gender order",
								helper: "Preferred gender order, most-preferred first. Empty = off.",
								children: /* @__PURE__ */ _(De, {
									options: Pn,
									values: B("Performers").GenderOrder,
									onChange: (e) => {
										R("Performers", { GenderOrder: e });
									},
									addPrompt: "Add a gender…"
								})
							}),
							/* @__PURE__ */ _(Ye, {
								label: "Whitelist",
								helper: "If set, only these performers are kept (case-insensitive).",
								values: B("Performers").Whitelist,
								onChange: (e) => {
									R("Performers", { Whitelist: e });
								},
								placeholder: "Search performers…"
							}),
							/* @__PURE__ */ _(Ye, {
								label: "Blacklist",
								helper: "These performers are removed (case-insensitive).",
								values: B("Performers").Blacklist,
								onChange: (e) => {
									R("Performers", { Blacklist: e });
								},
								placeholder: "Search performers…"
							})
						]
					}) : null,
					Le ? /* @__PURE__ */ v(q, {
						title: "Tags",
						badge: /* @__PURE__ */ _(Ne, {
							mono: !0,
							children: "$tags"
						}),
						accent: !0,
						children: [
							/* @__PURE__ */ _(H, {
								label: "Separator",
								children: /* @__PURE__ */ _(Ce, {
									value: B("Tags").Separator,
									onChange: (e) => {
										R("Tags", { Separator: e });
									},
									options: Rn,
									customPlaceholder: "Custom separator"
								})
							}),
							/* @__PURE__ */ _(H, {
								label: "Max count",
								helper: "0 = unlimited",
								children: /* @__PURE__ */ _(xe, {
									value: B("Tags").MaxCount,
									min: 0,
									onChange: (e) => {
										R("Tags", { MaxCount: e });
									}
								})
							}),
							/* @__PURE__ */ _(H, {
								label: "On overflow",
								children: /* @__PURE__ */ _(W, {
									value: B("Tags").OnOverflow,
									onChange: (e) => {
										R("Tags", { OnOverflow: e });
									},
									options: jn
								})
							}),
							/* @__PURE__ */ _(H, {
								label: "Sort",
								children: /* @__PURE__ */ _(W, {
									value: B("Tags").Sort,
									onChange: (e) => {
										R("Tags", { Sort: e });
									},
									options: Nn
								})
							}),
							/* @__PURE__ */ _(Je, {
								label: "Whitelist",
								helper: "If set, only these tags are kept (case-insensitive).",
								values: B("Tags").Whitelist,
								onChange: (e) => {
									R("Tags", { Whitelist: e });
								},
								placeholder: "Search tags…"
							}),
							/* @__PURE__ */ _(Je, {
								label: "Blacklist",
								helper: "These tags are removed (case-insensitive).",
								values: B("Tags").Blacklist,
								onChange: (e) => {
									R("Tags", { Blacklist: e });
								},
								placeholder: "Search tags…"
							})
						]
					}) : null,
					Z || Q ? /* @__PURE__ */ v(q, {
						accent: !0,
						badge: /* @__PURE__ */ _(Ne, {
							mono: !0,
							children: Z && Q ? "$date · $duration" : Z ? "$date" : "$duration"
						}),
						title: Z && Q ? "Date & duration format" : Z ? "Date format" : "Duration format",
						children: [Z ? /* @__PURE__ */ _(H, {
							label: "Date format",
							helper: "e.g. yyyy-MM-dd",
							children: /* @__PURE__ */ _(Se, {
								value: r.DateFormat,
								onChange: (e) => {
									L("DateFormat", e);
								},
								options: In,
								customPlaceholder: "yyyy-MM-dd"
							})
						}) : null, Q ? /* @__PURE__ */ _(H, {
							label: "Duration format",
							children: /* @__PURE__ */ _(Se, {
								value: r.DurationFormat,
								onChange: (e) => {
									L("DurationFormat", e);
								},
								options: Ln,
								customPlaceholder: "hh\\-mm\\-ss"
							})
						}) : null]
					}) : null,
					!be && !Le && !Z && !Q ? /* @__PURE__ */ v("div", {
						className: "rounded-xl border border-border bg-card p-6 text-center",
						children: [
							/* @__PURE__ */ _("h3", {
								className: "text-base font-semibold text-foreground",
								children: "No token-specific settings needed"
							}),
							/* @__PURE__ */ _("p", {
								className: "mx-auto mb-4 mt-1 max-w-md text-sm text-secondary",
								children: "Add $performers, $tags, $date, or $duration to your filename or folder template to configure how they're formatted."
							}),
							/* @__PURE__ */ v("div", {
								className: "flex flex-wrap justify-center gap-1",
								children: [
									/* @__PURE__ */ _(V, {
										selected: !1,
										mono: !0,
										onClick: () => {
											z("{ - $performers}");
										},
										children: "$performers"
									}),
									/* @__PURE__ */ _(V, {
										selected: !1,
										mono: !0,
										onClick: () => {
											z("{ - $tags}");
										},
										children: "$tags"
									}),
									/* @__PURE__ */ _(V, {
										selected: !1,
										mono: !0,
										onClick: () => {
											z("{ - $date}");
										},
										children: "$date"
									}),
									/* @__PURE__ */ _(V, {
										selected: !1,
										mono: !0,
										onClick: () => {
											z("{ [$duration]}");
										},
										children: "$duration"
									})
								]
							})
						]
					}) : null
				]
			}),
			/* @__PURE__ */ _(Pe, {
				title: "Destination routing",
				hint: "Where renamed files land"
			}),
			/* @__PURE__ */ v("div", {
				className: "space-y-4",
				children: [
					/* @__PURE__ */ v(K, {
						title: "Default & unorganized destinations",
						children: [/* @__PURE__ */ v("div", {
							className: "grid grid-cols-1 gap-4 md:grid-cols-2",
							children: [/* @__PURE__ */ v(H, {
								label: "Default destination",
								helper: "Where an item matching no rule goes. Blank = no default route. Honored only with the relocate gate below ON.",
								children: [/* @__PURE__ */ _(U, {
									value: r.DefaultDestination,
									onChange: (e) => {
										L("DefaultDestination", e);
									},
									placeholder: "Absolute root, or blank"
								}), /* @__PURE__ */ _(Me, { value: r.DefaultDestination })]
							}), /* @__PURE__ */ v(H, {
								label: "Unorganized destination",
								helper: "Where un-curated items route instead of being skipped. Blank = no unorganized route.",
								children: [/* @__PURE__ */ _(U, {
									value: r.UnorganizedDestination,
									onChange: (e) => {
										L("UnorganizedDestination", e);
									},
									placeholder: "Absolute root, or blank"
								}), /* @__PURE__ */ _(Me, { value: r.UnorganizedDestination })]
							})]
						}), /* @__PURE__ */ _(G, {
							label: "Relocate unmatched items to the default destination",
							checked: r.EnableDefaultRelocate,
							onChange: (e) => {
								L("EnableDefaultRelocate", e);
							},
							helper: "With this on, any item matching no rule is moved to the default destination — whole-library reach. Undo is the only recovery. Off by default."
						})]
					}),
					/* @__PURE__ */ _(Fe, {
						title: "Per-studio destinations",
						description: "Pick a studio, then the absolute root its items route to.",
						enabled: r.EnableStudioDestinations,
						onToggle: (e) => {
							L("EnableStudioDestinations", e);
						},
						children: /* @__PURE__ */ _($e, {
							map: r.StudioDestinations,
							onChange: (e) => {
								L("StudioDestinations", e);
							}
						})
					}),
					/* @__PURE__ */ _(Fe, {
						title: "Per-tag destinations",
						description: "Pick a tag, then the absolute root its items route to.",
						enabled: r.EnableTagDestinations,
						onToggle: (e) => {
							L("EnableTagDestinations", e);
						},
						children: /* @__PURE__ */ _(Ae, {
							map: r.TagDestinations,
							onChange: (e) => {
								L("TagDestinations", e);
							},
							renderKey: (e, t, n) => /* @__PURE__ */ _(Je, {
								label: "",
								values: e === "" ? [] : [e],
								onChange: (e) => {
									t(e.at(-1) ?? "");
								},
								placeholder: "Search tags…",
								excludeValues: n
							}),
							renderValue: (e, t) => /* @__PURE__ */ v(g, { children: [/* @__PURE__ */ _(U, {
								value: e,
								onChange: t,
								placeholder: "Destination root"
							}), /* @__PURE__ */ _(Me, { value: e })] }),
							addLabel: "Add tag rule"
						})
					}),
					/* @__PURE__ */ v(Fe, {
						title: "Advanced routing & safety",
						description: "Allowed roots and source-path rules.",
						enabled: r.EnableAdvancedRouting,
						onToggle: (e) => {
							L("EnableAdvancedRouting", e);
						},
						children: [/* @__PURE__ */ v("div", { children: [
							/* @__PURE__ */ _("h4", {
								className: "text-sm font-semibold text-foreground",
								children: "Allowed roots"
							}),
							/* @__PURE__ */ _("p", {
								className: "mb-4 mt-1 text-sm text-secondary",
								children: "A rename may only write inside these absolute directories; a target outside them is rejected. Empty = files stay within their own source folder."
							}),
							/* @__PURE__ */ _(Te, {
								values: r.AllowedRoots,
								onChange: (e) => {
									L("AllowedRoots", e);
								},
								placeholder: "Add an absolute directory, press Enter"
							})
						] }), /* @__PURE__ */ v("div", { children: [
							/* @__PURE__ */ _("h4", {
								className: "text-sm font-semibold text-foreground",
								children: "Source-path destinations"
							}),
							/* @__PURE__ */ _("p", {
								className: "mb-4 mt-1 text-sm text-secondary",
								children: "Match an item's source path to a destination root, top rule first. An exact match or a regex."
							}),
							/* @__PURE__ */ _(ke, {
								rows: r.PathDestinations,
								onChange: (e) => {
									L("PathDestinations", e);
								},
								makeRow: () => ({
									Pattern: "",
									Dest: "",
									IsRegex: !1
								}),
								renderRow: (e, t, n) => /* @__PURE__ */ v(g, { children: [
									/* @__PURE__ */ _(H, {
										label: "Source path",
										children: /* @__PURE__ */ _(U, {
											value: e.Pattern,
											onChange: (e) => {
												n({ Pattern: e });
											},
											mono: !0,
											placeholder: "Exact path or regex"
										})
									}),
									/* @__PURE__ */ _(G, {
										label: "Match as a regex",
										checked: e.IsRegex,
										onChange: (e) => {
											n({ IsRegex: e });
										}
									}),
									/* @__PURE__ */ _(je, {
										pattern: e.Pattern,
										isRegex: e.IsRegex
									}),
									/* @__PURE__ */ v(H, {
										label: "Destination root",
										children: [/* @__PURE__ */ _(U, {
											value: e.Dest,
											onChange: (e) => {
												n({ Dest: e });
											},
											placeholder: "Destination root"
										}), /* @__PURE__ */ _(Me, { value: e.Dest })]
									})
								] }),
								addLabel: "Add path rule",
								ordered: !0
							})
						] })]
					}),
					/* @__PURE__ */ _(K, {
						title: "Sidecar files",
						description: "Files sharing the primary's basename with one of these extensions move and rename with it; a target that already exists is left untouched, never overwritten. Captions Cove tracks always move regardless.",
						children: /* @__PURE__ */ v(H, {
							label: "Also move sidecar files with these extensions",
							children: [/* @__PURE__ */ _(Te, {
								values: r.AssociatedExtensions,
								onChange: (e) => {
									L("AssociatedExtensions", e);
								},
								placeholder: "Add an extension, press Enter",
								normalize: kn,
								onReject: (e) => !/^[a-z0-9]+$/.test(e),
								onLiveChange: (e) => {
									M(e);
								}
							}), (() => {
								let e = pe(kn(j));
								return e ? /* @__PURE__ */ _(Y, {
									kind: "warning",
									children: e
								}) : null;
							})()]
						})
					}),
					/* @__PURE__ */ _("div", {
						className: "rounded-xl border border-border bg-card p-4",
						children: /* @__PURE__ */ _(G, {
							label: "Delete the source folder when a move leaves it empty",
							checked: r.RemoveEmptyFolder,
							onChange: (e) => {
								L("RemoveEmptyFolder", e);
							},
							helper: "Deletes a source folder only when a move empties it completely — never a non-empty folder or a root. Undo won't move the file back into a deleted folder; the file stays at its new location. Off by default."
						})
					})
				]
			}),
			/* @__PURE__ */ _(Pe, {
				title: "Advanced",
				hint: "Power-user controls — collapsed by default"
			}),
			/* @__PURE__ */ v("div", {
				className: "space-y-3",
				children: [
					/* @__PURE__ */ v(Ie, {
						title: "Clean up the name",
						summary: "Illegal-character and space handling, case, ASCII",
						children: [/* @__PURE__ */ v("div", {
							className: "grid grid-cols-1 gap-4 md:grid-cols-2",
							children: [
								/* @__PURE__ */ _(H, {
									label: "Illegal-char replacement",
									children: /* @__PURE__ */ _(we, {
										value: r.IllegalReplacement,
										onChange: (e) => {
											L("IllegalReplacement", e);
										},
										stripLabel: "Strip",
										replaceLabel: "Replace with",
										stripHelper: "Illegal characters are removed.",
										replaceHelper: "Each illegal character becomes this.",
										inputPlaceholder: "e.g. _"
									})
								}),
								/* @__PURE__ */ _(H, {
									label: "Space replacement",
									children: /* @__PURE__ */ _(we, {
										value: r.SpaceReplacement,
										onChange: (e) => {
											L("SpaceReplacement", e);
										},
										stripLabel: "Keep spaces",
										replaceLabel: "Replace with",
										stripHelper: "Spaces are left as-is.",
										replaceHelper: "Each space becomes this.",
										inputPlaceholder: "e.g. _ or ."
									})
								}),
								/* @__PURE__ */ _(H, {
									label: "Remove characters",
									helper: "Characters to delete from the name, e.g. ,# — separate from illegal-character handling.",
									children: /* @__PURE__ */ _(U, {
										value: r.RemoveCharacters,
										onChange: (e) => {
											L("RemoveCharacters", e);
										},
										placeholder: "e.g. ,#"
									})
								}),
								/* @__PURE__ */ _(H, {
									label: "Case",
									children: /* @__PURE__ */ _(W, {
										value: r.Case,
										onChange: (e) => {
											L("Case", e);
										},
										options: An
									})
								})
							]
						}), /* @__PURE__ */ _(G, {
							label: "ASCII transliterate",
							checked: r.AsciiTransliterate,
							onChange: (e) => {
								L("AsciiTransliterate", e);
							},
							helper: "Convert accented characters to plain ASCII."
						})]
					}),
					/* @__PURE__ */ v(Ie, {
						title: "Length & collisions",
						summary: "Length caps, what to drop when too long, duplicate suffix",
						children: [
							/* @__PURE__ */ v("div", {
								className: "grid grid-cols-1 gap-4 md:grid-cols-2",
								children: [/* @__PURE__ */ _(H, {
									label: "Filename max length",
									children: /* @__PURE__ */ _(xe, {
										value: r.FilenameMax,
										min: 1,
										onChange: (e) => {
											L("FilenameMax", e);
										}
									})
								}), /* @__PURE__ */ _(H, {
									label: "Full-path max length",
									children: /* @__PURE__ */ _(xe, {
										value: r.FullPathMax,
										min: 1,
										onChange: (e) => {
											L("FullPathMax", e);
										}
									})
								})]
							}),
							/* @__PURE__ */ v(H, {
								label: "Drop order",
								helper: "Fields dropped (top first) when the name is too long.",
								children: [
									/* @__PURE__ */ _(Te, {
										values: r.DropOrder,
										onChange: (e) => {
											L("DropOrder", e);
										},
										ordered: !0,
										placeholder: "Add field, press Enter"
									}),
									/* @__PURE__ */ _(Oe, {
										tokens: pn,
										values: r.DropOrder,
										onAdd: (e) => {
											L("DropOrder", r.DropOrder.includes(e) ? r.DropOrder : [...r.DropOrder, e]);
										}
									}),
									/* @__PURE__ */ _(Vn, { values: r.DropOrder })
								]
							}),
							/* @__PURE__ */ _(H, {
								label: "Duplicate suffix format",
								helper: "{n} = a counter added only when a name already exists, e.g. name (1).mp4.",
								children: /* @__PURE__ */ _(Se, {
									value: r.DuplicateSuffixFormat,
									onChange: (e) => {
										L("DuplicateSuffixFormat", e);
									},
									options: zn,
									customPlaceholder: " ({n})"
								})
							})
						]
					}),
					/* @__PURE__ */ v(Ie, {
						title: "Excludes",
						summary: "Skip items by tag, studio, or source path — evaluated before any routing",
						children: [
							/* @__PURE__ */ _(K, {
								title: "Exclude by tag",
								description: "An item carrying any of these tags is skipped — never renamed, never moved. Evaluated before any routing rule.",
								children: /* @__PURE__ */ _(Je, {
									label: "Tags",
									values: r.ExcludeTags,
									onChange: (e) => {
										L("ExcludeTags", e);
									},
									placeholder: "Search tags…"
								})
							}),
							/* @__PURE__ */ _(K, {
								title: "Exclude by studio",
								description: "An item under any of these studios — or under a child of one — is skipped entirely. Evaluated before any routing rule.",
								children: /* @__PURE__ */ _(qe, {
									label: "Studios",
									values: r.ExcludeStudioIds,
									onChange: (e) => {
										L("ExcludeStudioIds", e);
									},
									placeholder: "Search studios…"
								})
							}),
							/* @__PURE__ */ _(K, {
								title: "Exclude by source path",
								description: "An item whose source path matches a rule is skipped entirely. Evaluated before any routing rule. An exact match or a regex.",
								children: /* @__PURE__ */ _(ke, {
									rows: r.ExcludePaths,
									onChange: (e) => {
										L("ExcludePaths", e);
									},
									makeRow: () => ({
										Pattern: "",
										IsRegex: !1
									}),
									renderRow: (e, t, n) => /* @__PURE__ */ v(g, { children: [
										/* @__PURE__ */ _(H, {
											label: "Source path",
											children: /* @__PURE__ */ _(U, {
												value: e.Pattern,
												onChange: (e) => {
													n({ Pattern: e });
												},
												mono: !0,
												placeholder: "Exact path or regex"
											})
										}),
										/* @__PURE__ */ _(G, {
											label: "Match as a regex",
											checked: e.IsRegex,
											onChange: (e) => {
												n({ IsRegex: e });
											}
										}),
										/* @__PURE__ */ _(je, {
											pattern: e.Pattern,
											isRegex: e.IsRegex
										})
									] }),
									addLabel: "Add exclude rule"
								})
							})
						]
					}),
					/* @__PURE__ */ v(Ie, {
						title: "Field rewriting & name shaping",
						summary: "Literal token replacements, article stripping, and name shaping",
						children: [
							/* @__PURE__ */ _(K, {
								title: "Per-token replacements",
								description: "A literal find/replace on a single token's value, before the name is shaped. The target is a canonical token name (e.g. studio, title), matched case-insensitively.",
								children: /* @__PURE__ */ _(ke, {
									rows: r.FieldReplacers,
									onChange: (e) => {
										L("FieldReplacers", e);
									},
									makeRow: () => ({
										TargetToken: Fn[0].value,
										Find: "",
										Replace: ""
									}),
									renderRow: (e, t, n) => {
										let r = Fn.some((t) => t.value === e.TargetToken) ? Fn : [...Fn, {
											value: e.TargetToken,
											label: `${e.TargetToken} (unknown)`
										}];
										return /* @__PURE__ */ v(g, { children: [
											/* @__PURE__ */ _(H, {
												label: "Target token",
												children: /* @__PURE__ */ _(W, {
													value: e.TargetToken,
													onChange: (e) => {
														n({ TargetToken: e });
													},
													options: r
												})
											}),
											/* @__PURE__ */ _(H, {
												label: "Find",
												helper: "Literal text to match. Empty does nothing.",
												children: /* @__PURE__ */ _(U, {
													value: e.Find,
													onChange: (e) => {
														n({ Find: e });
													},
													placeholder: "Text to find"
												})
											}),
											/* @__PURE__ */ _(H, {
												label: "Replace with",
												children: /* @__PURE__ */ _(U, {
													value: e.Replace,
													onChange: (e) => {
														n({ Replace: e });
													},
													placeholder: "Replacement (blank to remove)"
												})
											})
										] });
									},
									addLabel: "Add replacement"
								})
							}),
							/* @__PURE__ */ v(K, {
								title: "Strip leading article",
								children: [/* @__PURE__ */ _(G, {
									label: "Strip a leading article from the title",
									checked: r.StripLeadingArticles,
									onChange: (e) => {
										L("StripLeadingArticles", e);
									},
									helper: "Removes a single leading article and the whitespace after it from the title, at most once (case-insensitive) — a word merely starting with an article, and a mid-title article, are left alone."
								}), /* @__PURE__ */ _(H, {
									label: "Articles",
									children: /* @__PURE__ */ _(Te, {
										values: r.Articles,
										onChange: (e) => {
											L("Articles", e);
										},
										placeholder: "Add article, press Enter"
									})
								})]
							}),
							/* @__PURE__ */ _(G, {
								label: "Squeeze studio names",
								checked: r.SqueezeStudioNames,
								onChange: (e) => {
									L("SqueezeStudioNames", e);
								},
								helper: "Removes all spaces from the studio value so one studio renders to one stable folder name."
							}),
							/* @__PURE__ */ _(G, {
								label: "Drop a performer already in the title",
								checked: r.PreventTitlePerformer,
								onChange: (e) => {
									L("PreventTitlePerformer", e);
								},
								helper: "Drops a performer whose name already appears as a whole word in the title."
							}),
							/* @__PURE__ */ _(G, {
								label: "Collapse repeated folder segments",
								checked: r.PreventConsecutiveSegments,
								onChange: (e) => {
									L("PreventConsecutiveSegments", e);
								},
								helper: "Collapses consecutive duplicate folder path segments to one — affects the folder path, not the filename."
							})
						]
					})
				]
			}),
			/* @__PURE__ */ _("div", {
				id: "rename-undo-section",
				children: /* @__PURE__ */ _(bt, { refreshKey: 0 })
			}),
			/* @__PURE__ */ _(Un, {
				dirty: ae,
				saving: m,
				saveError: y,
				savedFlash: x,
				canSave: oe,
				onSave: () => void ve(),
				onDiscard: () => {
					i(c);
				}
			})
		]
	});
}
//#endregion
//#region src/RenamePage.tsx
function Kn() {
	return /* @__PURE__ */ _(Gn, {});
}
//#endregion
//#region src/preview.ts
function qn(e) {
	if (!e) return e;
	let t = Math.max(e.lastIndexOf("/"), e.lastIndexOf("\\"));
	return t >= 0 ? e.slice(t + 1) : e;
}
var Jn = 5;
function Yn(e) {
	let t = e / (1024 * 1024 * 1024);
	return t >= 10 ? `${Math.round(t)} GB` : `${t.toFixed(1)} GB`;
}
function Xn(e) {
	return (e?.volumePairs ?? []).map((e) => `↪ ${e.count} item${e.count === 1 ? "" : "s"} (${Yn(e.bytes)}) move from ${e.from} to ${e.to}.`);
}
function Zn(e) {
	return e === "Heavy" ? "This is a LARGE cross-drive move — files will be COPIED across drives, which can take a while. Click OK only if you are sure; Cancel to stop. You can undo this afterwards." : e === "Standard" ? "This moves files across drives. Click OK to proceed, or Cancel to stop. You can undo this afterwards." : "Click OK to rename, or Cancel to stop. You can undo this afterwards.";
}
function Qn(e, t) {
	let n = e.filter((e) => e.status === "Renamer" || e.status === "Move"), r = n.length, i = e.length, a = e.filter((e) => e.status === "SkipGated").length, o = e.filter((e) => e.status === "SkipCollision").length, s = e.filter((e) => e.status === "SkipLocked").length, c = a + o + s, l = n.filter((e) => e.suffixed).length, u = n.filter((e) => e.sanitized).length, d = [];
	if (c > 0) {
		let e = [];
		if (a > 0 && e.push(`${a} need a required field`), o > 0 && e.push(`${o} have a name conflict`), s > 0 && e.push(`${s} are in use`), e.length === 1) {
			let e = a > 0 ? "needs a required field" : o > 0 ? "name conflict" : "in use";
			d.push(`⚠ ${c} skipped (${e}).`);
		} else d.push(`⚠ ${c} skipped — ${e.join(", ")}.`);
	}
	u > 0 && d.push(`⚠ ${u} had illegal characters cleaned up.`), l > 0 && d.push(`⚠ ${l} got a number added to avoid a name clash (e.g. "name (1)").`);
	let f = Xn(t), p = d.length > 0 ? `${d.join("\n")}\n\n` : "", m = f.length > 0 ? `${f.join("\n")}\n\n` : "";
	if (r === 0) return {
		text: `Nothing will be renamed — all ${i} selected item${i === 1 ? "" : "s"} are skipped or already named correctly.\n\n` + p + "Click OK to dismiss.",
		willRenameCount: 0
	};
	let h = r === i ? `Rename ${r} selected item${r === 1 ? "" : "s"}?` : `Rename ${r} of ${i} selected items?`, g = n.slice(0, Jn).map((e) => `  ${qn(e.oldFullPath)}  →  ${e.newBasename || qn(e.newFullPath)}`), _ = r - g.length;
	_ > 0 && g.push(`  … and ${_} more.`);
	let v = Zn(t?.confirmLevel ?? "Light");
	return {
		text: `${h}\n\n` + p + m + `Examples:\n${g.join("\n")}\n\n` + v,
		willRenameCount: r
	};
}
//#endregion
//#region src/renameSelected.ts
var $n = "com.alextomas955.renamer", er = `/extensions/${$n}/preview`, tr = `/extensions/${$n}/renamer`;
async function nr(e, t) {
	let n = JSON.stringify({
		EntityType: t.entityType,
		EntityIds: t.entityIds
	}), r = await S(er, {
		method: "POST",
		body: n
	}), { text: i, willRenameCount: a } = Qn(r.items, r.summary);
	if (!window.confirm(i) || a === 0) return { cancelled: !0 };
	try {
		await S(tr, {
			method: "POST",
			body: n
		});
	} catch (e) {
		if (e instanceof C) throw e;
	}
	return {};
}
//#endregion
//#region src/index.ts
var rr = b({ components: { RenamerPage: Kn } });
rr.actionHandlers = { renamerSelected: nr };
//#endregion
export { rr as default };
