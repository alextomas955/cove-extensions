import { useCallback as e, useEffect as t, useId as n, useMemo as r, useRef as i, useState as a } from "react";
import { AlertTriangle as o, ChevronDown as s, ChevronUp as c, Loader2 as l, Undo2 as u, X as d } from "lucide-react";
import { Fragment as f, jsx as p, jsxs as m } from "react/jsx-runtime";
//#region node_modules/@cove/extension-sdk/dist/define.js
function h(e) {
	return e;
}
//#endregion
//#region node_modules/@cove/extension-sdk/dist/api.js
var g = "/api";
async function _(e, t = {}) {
	let n = `${g}${e}`, r = await fetch(n, {
		...t,
		headers: {
			"Content-Type": "application/json",
			...t.headers
		}
	});
	if (!r.ok) {
		let t = await r.text().catch(() => "");
		throw new v(r.status, t || r.statusText, e);
	}
	if (r.status !== 204) return r.json();
}
var v = class extends Error {
	status;
	body;
	path;
	constructor(e, t, n) {
		super(`API ${e} ${n}: ${t}`), this.status = e, this.body = t, this.path = n, this.name = "ApiError";
	}
};
function y(e) {
	let t = `/extensions/${e}/data`;
	return {
		get: (e) => _(`${t}/${encodeURIComponent(e)}`).then((e) => e.value),
		set: (e, n) => _(t, {
			method: "POST",
			body: JSON.stringify({
				key: e,
				value: n
			})
		}),
		delete: (e) => _(`${t}/${encodeURIComponent(e)}`, { method: "DELETE" }),
		getAll: () => _(`${t}`)
	};
}
//#endregion
//#region node_modules/@cove/extension-sdk/dist/hooks.js
function b(e) {
	return r(() => y(e), [e]);
}
//#endregion
//#region src/options.ts
var x = {
	FilenameTemplate: "{$date - }$title{ [$height]}",
	FolderTemplate: "",
	DateFormat: "yyyy-MM-dd",
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
	Articles: [
		"The",
		"A",
		"An"
	],
	PreventTitlePerformer: !1,
	PreventConsecutiveSegments: !0
};
function S() {
	return {
		...x,
		Performers: {
			...x.Performers,
			Whitelist: [],
			Blacklist: [],
			IgnoreGenders: [],
			GenderOrder: []
		},
		Tags: {
			...x.Tags,
			Whitelist: [],
			Blacklist: [],
			IgnoreGenders: [],
			GenderOrder: []
		},
		DropOrder: [...x.DropOrder],
		RequiredFields: [...x.RequiredFields],
		StudioDestinations: { ...x.StudioDestinations },
		TagDestinations: { ...x.TagDestinations },
		PathDestinations: x.PathDestinations.map((e) => ({ ...e })),
		ExcludeTags: [...x.ExcludeTags],
		ExcludeStudioIds: [...x.ExcludeStudioIds],
		ExcludePaths: x.ExcludePaths.map((e) => ({ ...e })),
		AllowedRoots: [...x.AllowedRoots],
		AssociatedExtensions: [...x.AssociatedExtensions],
		FieldReplacers: x.FieldReplacers.map((e) => ({ ...e })),
		Articles: [...x.Articles]
	};
}
function C(e) {
	return e && typeof e == "object" ? e : {};
}
function w(e, t) {
	return typeof e == "string" ? e : t;
}
function T(e, t) {
	return typeof e == "number" && Number.isFinite(e) ? e : t;
}
function E(e, t) {
	return typeof e == "boolean" ? e : t;
}
function D(e, t) {
	return Array.isArray(e) ? e.filter((e) => typeof e == "string") : t;
}
function O(e, t) {
	return Array.isArray(e) ? e.filter((e) => typeof e == "number" && Number.isFinite(e)) : t;
}
function k(e) {
	let t = C(e), n = {};
	for (let [e, r] of Object.entries(t)) {
		let t = Number(e);
		Number.isInteger(t) && typeof r == "string" && (n[t] = r);
	}
	return n;
}
function A(e) {
	let t = C(e), n = {};
	for (let [e, r] of Object.entries(t)) typeof r == "string" && (n[e] = r);
	return n;
}
function ee(e) {
	return Array.isArray(e) ? e.filter((e) => e && typeof e == "object").map((e) => {
		let t = e;
		return {
			Pattern: w(t.Pattern, ""),
			Dest: w(t.Dest, ""),
			IsRegex: E(t.IsRegex, !1)
		};
	}) : [];
}
function te(e) {
	return Array.isArray(e) ? e.filter((e) => e && typeof e == "object").map((e) => {
		let t = e;
		return {
			Pattern: w(t.Pattern, ""),
			IsRegex: E(t.IsRegex, !1)
		};
	}) : [];
}
function j(e) {
	return Array.isArray(e) ? e.filter((e) => e && typeof e == "object").map((e) => {
		let t = e;
		return {
			TargetToken: w(t.TargetToken, ""),
			Find: w(t.Find, ""),
			Replace: w(t.Replace, "")
		};
	}) : [];
}
function M(e) {
	return e === "KeepFirst" ? "KeepFirst" : "DropAll";
}
function ne(e) {
	return e === "None" || e === "IdAsc" || e === "FavoriteFirst" ? e : "NameAsc";
}
function N(e) {
	return e === "Lower" || e === "Title" ? e : "None";
}
function re(e, t) {
	let n = C(e);
	return {
		Separator: w(n.Separator, t.Separator),
		MaxCount: T(n.MaxCount, t.MaxCount),
		OnOverflow: M(n.OnOverflow),
		Sort: ne(n.Sort),
		Whitelist: D(n.Whitelist, []),
		Blacklist: D(n.Blacklist, []),
		IgnoreGenders: D(n.IgnoreGenders, []),
		GenderOrder: D(n.GenderOrder, [])
	};
}
var ie = new Set(Object.keys(x));
function ae(e) {
	if (!e || typeof e != "object") return {};
	let t = {};
	for (let [n, r] of Object.entries(e)) ie.has(n) || (t[n] = r);
	return t;
}
function oe(e) {
	if (!e || typeof e != "object") return S();
	let t = e, n = x;
	return {
		FilenameTemplate: w(t.FilenameTemplate, n.FilenameTemplate),
		FolderTemplate: w(t.FolderTemplate, n.FolderTemplate),
		DateFormat: w(t.DateFormat, n.DateFormat),
		DurationFormat: w(t.DurationFormat, n.DurationFormat),
		Performers: re(t.Performers, n.Performers),
		Tags: re(t.Tags, n.Tags),
		IllegalReplacement: w(t.IllegalReplacement, n.IllegalReplacement),
		SpaceReplacement: w(t.SpaceReplacement, n.SpaceReplacement),
		RemoveCharacters: w(t.RemoveCharacters, n.RemoveCharacters),
		Case: N(t.Case),
		AsciiTransliterate: E(t.AsciiTransliterate, n.AsciiTransliterate),
		FilenameMax: T(t.FilenameMax, n.FilenameMax),
		FullPathMax: T(t.FullPathMax, n.FullPathMax),
		DropOrder: D(t.DropOrder, [...n.DropOrder]),
		OnlyOrganized: E(t.OnlyOrganized, n.OnlyOrganized),
		FilenameAsTitle: E(t.FilenameAsTitle, n.FilenameAsTitle),
		RequiredFields: D(t.RequiredFields, [...n.RequiredFields]),
		DuplicateSuffixFormat: w(t.DuplicateSuffixFormat, n.DuplicateSuffixFormat),
		AutoRenamerOnUpdate: E(t.AutoRenamerOnUpdate, n.AutoRenamerOnUpdate),
		StudioDestinations: k(t.StudioDestinations),
		TagDestinations: A(t.TagDestinations),
		PathDestinations: ee(t.PathDestinations),
		ExcludeTags: D(t.ExcludeTags, []),
		ExcludeStudioIds: O(t.ExcludeStudioIds, []),
		ExcludePaths: te(t.ExcludePaths),
		AllowedRoots: D(t.AllowedRoots, []),
		AssociatedExtensions: D(t.AssociatedExtensions, [...n.AssociatedExtensions]),
		DefaultDestination: w(t.DefaultDestination, n.DefaultDestination),
		UnorganizedDestination: w(t.UnorganizedDestination, n.UnorganizedDestination),
		EnableDefaultRelocate: E(t.EnableDefaultRelocate, n.EnableDefaultRelocate),
		EnableStudioDestinations: E(t.EnableStudioDestinations, n.EnableStudioDestinations),
		EnableTagDestinations: E(t.EnableTagDestinations, n.EnableTagDestinations),
		EnableAdvancedRouting: E(t.EnableAdvancedRouting, n.EnableAdvancedRouting),
		RemoveEmptyFolder: E(t.RemoveEmptyFolder, n.RemoveEmptyFolder),
		SqueezeStudioNames: E(t.SqueezeStudioNames, n.SqueezeStudioNames),
		FieldReplacers: j(t.FieldReplacers),
		StripLeadingArticles: E(t.StripLeadingArticles, n.StripLeadingArticles),
		Articles: D(t.Articles, [...n.Articles]),
		PreventTitlePerformer: E(t.PreventTitlePerformer, n.PreventTitlePerformer),
		PreventConsecutiveSegments: E(t.PreventConsecutiveSegments, n.PreventConsecutiveSegments)
	};
}
//#endregion
//#region src/primitivesLogic.ts
function se(e) {
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
function ce(e) {
	let t = e.trim();
	return t.length === 0 ? !0 : /^[A-Za-z]:[\\/]/.test(t) || /^[\\/]/.test(t);
}
var P = /* @__PURE__ */ new Set([
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
function le(e) {
	return e.length === 0 ? null : /^[a-z0-9]+$/.test(e) ? P.has(e) ? "This looks like a primary media extension, not a sidecar." : null : "Extensions are letters and numbers only, like srt or nfo.";
}
//#endregion
//#region src/entityPickerLogic.ts
function ue(e, t) {
	let n = e.trim().toLowerCase();
	return n.length === 0 ? [...t] : t.filter((e) => e.name.toLowerCase().includes(n));
}
function de(e, t, n) {
	if (t.length === 0) return [...e];
	let r = new Set(t);
	return e.filter((e) => !r.has(n(e)));
}
function fe(e, t) {
	let n = new Set(t);
	return e.filter((e) => !n.has(e.value));
}
function pe(e, t) {
	let n = t.find((t) => t.id === e);
	return n ? n.name : `#${e} (missing)`;
}
function me(e, t) {
	return t.some((t) => t.id === e);
}
function F(e, t) {
	let n = e.trim(), r = t.find((e) => e.name.toLowerCase() === n.toLowerCase());
	return r ? r.name : n;
}
//#endregion
//#region src/primitives.tsx
var I = "w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none", he = "cursor-pointer rounded-lg border px-2 py-1 text-xs", ge = "border-border bg-card text-foreground hover:border-accent/50 hover:text-accent", _e = "border-accent bg-accent/15 text-foreground";
function ve(e) {
	return `${he} ${e ? _e : ge}`;
}
function L({ selected: e, onClick: t, disabled: n, title: r, mono: i, children: a }) {
	return /* @__PURE__ */ p("button", {
		type: "button",
		onClick: t,
		disabled: n,
		title: r,
		className: i ? `${ve(e)} font-mono` : ve(e),
		children: a
	});
}
var R = "__custom__";
function z({ label: e, helper: t, children: n }) {
	return /* @__PURE__ */ m("label", {
		className: "block text-sm",
		title: t,
		children: [
			e ? /* @__PURE__ */ p("span", {
				className: "mb-1 block text-xs font-medium uppercase tracking-wide text-muted",
				children: e
			}) : null,
			n,
			t ? /* @__PURE__ */ p("span", {
				className: "mt-1 block text-xs text-secondary",
				children: t
			}) : null
		]
	});
}
function B({ value: e, onChange: t, onFocus: n, placeholder: r, mono: i = !1, inputRef: a }) {
	return /* @__PURE__ */ p("input", {
		ref: a,
		type: "text",
		value: e,
		placeholder: r,
		onChange: (e) => {
			t(e.target.value);
		},
		onFocus: n,
		className: i ? `${I} font-mono` : I
	});
}
function ye({ value: e, onChange: t, min: n }) {
	return /* @__PURE__ */ p("input", {
		type: "number",
		value: Number.isNaN(e) ? "" : e,
		min: n,
		onChange: (e) => {
			t(e.target.value === "" ? 0 : Number(e.target.value));
		},
		className: `themed-number-input ${I}`
	});
}
function V({ value: e, onChange: t, options: n }) {
	return /* @__PURE__ */ p("select", {
		value: e,
		onChange: (e) => {
			t(e.target.value);
		},
		className: I,
		children: n.map((e) => /* @__PURE__ */ p("option", {
			value: e.value,
			children: e.label
		}, e.value))
	});
}
function be({ value: e, onChange: t, options: n, customPlaceholder: r }) {
	let i = n.find((t) => t.value === e), a = i === void 0, o = a ? R : e, s = i ? `${i.value} → ${i.example}` : e;
	return /* @__PURE__ */ m("div", { children: [/* @__PURE__ */ m("select", {
		value: o,
		onChange: (e) => {
			let n = e.target.value;
			n === R ? a || t("") : t(n);
		},
		className: I,
		children: [n.map((e) => /* @__PURE__ */ m("option", {
			value: e.value,
			children: [
				e.value,
				" → ",
				e.example
			]
		}, e.value)), /* @__PURE__ */ p("option", {
			value: R,
			children: "Custom…"
		})]
	}), a ? /* @__PURE__ */ p("div", {
		className: "mt-2",
		children: /* @__PURE__ */ p(B, {
			value: e,
			onChange: t,
			placeholder: r,
			mono: !0
		})
	}) : /* @__PURE__ */ p("span", {
		className: "mt-1 block font-mono text-xs text-secondary",
		children: s
	})] });
}
function xe({ value: e, onChange: t, options: n, customPlaceholder: r }) {
	let i = !n.some((t) => t.value === e);
	return /* @__PURE__ */ m("div", { children: [/* @__PURE__ */ m("div", {
		className: "flex flex-wrap gap-1",
		children: [n.map((n) => /* @__PURE__ */ p(L, {
			selected: n.value === e,
			onClick: () => {
				t(n.value);
			},
			children: n.label
		}, n.value || "__empty__")), /* @__PURE__ */ p(L, {
			selected: i,
			onClick: () => {
				i || t("");
			},
			children: "Custom"
		})]
	}), i ? /* @__PURE__ */ p("div", {
		className: "mt-2",
		children: /* @__PURE__ */ p(B, {
			value: e,
			onChange: t,
			placeholder: r,
			mono: !0
		})
	}) : null] });
}
function Se({ value: e, onChange: n, stripLabel: r, replaceLabel: o, stripHelper: s, replaceHelper: c, inputPlaceholder: l }) {
	let u = i(null), [d, f] = a(e !== ""), h = i(e);
	t(() => {
		e === "" ? h.current !== "" && f(!1) : f(!0), h.current = e;
	}, [e]);
	let g = d || e !== "";
	function _() {
		f(!1), e !== "" && n("");
	}
	function v() {
		f(!0), requestAnimationFrame(() => u.current?.focus());
	}
	return /* @__PURE__ */ m("div", { children: [/* @__PURE__ */ m("div", {
		className: "flex gap-1",
		children: [/* @__PURE__ */ p(L, {
			selected: !g,
			onClick: _,
			children: r
		}), /* @__PURE__ */ p(L, {
			selected: g,
			onClick: v,
			children: o
		})]
	}), g ? /* @__PURE__ */ m("div", {
		className: "mt-2",
		children: [/* @__PURE__ */ p(B, {
			value: e,
			onChange: n,
			placeholder: l,
			inputRef: u,
			mono: !0
		}), c ? /* @__PURE__ */ p("span", {
			className: "mt-1 block text-xs text-secondary",
			children: c
		}) : null]
	}) : s ? /* @__PURE__ */ p("span", {
		className: "mt-1 block text-xs text-secondary",
		children: s
	}) : null] });
}
function H({ label: e, checked: t, onChange: n, helper: r, ariaLabel: i }) {
	return /* @__PURE__ */ m("div", { children: [/* @__PURE__ */ m("label", {
		className: "flex items-center gap-2 text-sm text-secondary",
		title: r,
		children: [/* @__PURE__ */ p("button", {
			type: "button",
			role: "switch",
			"aria-checked": t,
			"aria-label": e ? void 0 : i,
			onClick: () => {
				n(!t);
			},
			className: `inline-flex h-5 w-9 items-center rounded-full transition-colors ${t ? "bg-accent" : "bg-card border border-border"}`,
			children: /* @__PURE__ */ p("span", {
				className: "inline-block h-4 w-4 rounded-full bg-white transition-transform",
				style: { transform: t ? "translateX(1rem)" : "translateX(0.125rem)" }
			})
		}), e ? /* @__PURE__ */ p("span", { children: e }) : null]
	}), r ? /* @__PURE__ */ p("p", {
		className: "mt-1 text-xs text-secondary",
		children: r
	}) : null] });
}
function Ce({ values: e, onChange: t, placeholder: r, ordered: i = !1, normalize: a, onReject: o, onLiveChange: s }) {
	let c = n();
	function l(n) {
		let r = (a ? a(n.value) : n.value).trim();
		r.length !== 0 && (o?.(r) || (e.includes(r) || t([...e, r]), n.value = ""));
	}
	function u(n) {
		t(e.filter((e, t) => t !== n));
	}
	function h(n, r) {
		let i = n + r;
		if (i < 0 || i >= e.length) return;
		let a = [...e];
		[a[n], a[i]] = [a[i], a[n]], t(a);
	}
	return /* @__PURE__ */ m("div", { children: [e.length > 0 ? /* @__PURE__ */ p("div", {
		className: "mb-1 flex flex-wrap gap-1",
		children: e.map((e, t) => /* @__PURE__ */ m("span", {
			className: "inline-flex items-center gap-1 rounded-lg border border-border bg-card px-2 py-0.5 text-xs text-foreground",
			children: [
				i ? /* @__PURE__ */ m(f, { children: [/* @__PURE__ */ p("button", {
					type: "button",
					"aria-label": `Move ${e} up`,
					onClick: () => {
						h(t, -1);
					},
					className: "text-muted hover:text-foreground",
					children: "↑"
				}), /* @__PURE__ */ p("button", {
					type: "button",
					"aria-label": `Move ${e} down`,
					onClick: () => {
						h(t, 1);
					},
					className: "text-muted hover:text-foreground",
					children: "↓"
				})] }) : null,
				/* @__PURE__ */ p("span", {
					className: "font-mono",
					children: e
				}),
				/* @__PURE__ */ p("button", {
					type: "button",
					"aria-label": `Remove ${e}`,
					onClick: () => {
						u(t);
					},
					className: "text-muted hover:text-foreground",
					children: /* @__PURE__ */ p(d, { className: "h-3 w-3" })
				})
			]
		}, `${e}-${t}`))
	}) : null, /* @__PURE__ */ p("input", {
		id: c,
		type: "text",
		placeholder: r,
		className: I,
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
function we({ options: e, values: t, onChange: n }) {
	let r = new Set(e.map((e) => e.value)), i = t.filter((e) => !r.has(e));
	function a(r) {
		let a = t.includes(r);
		n([...e.map((e) => e.value).filter((e) => e === r ? !a : t.includes(e)), ...i]);
	}
	function o(e) {
		n(t.filter((t) => t !== e));
	}
	return /* @__PURE__ */ m("div", {
		className: "flex flex-wrap gap-1",
		children: [e.map((e) => /* @__PURE__ */ p(L, {
			selected: t.includes(e.value),
			onClick: () => {
				a(e.value);
			},
			children: e.label
		}, e.value)), i.map((e) => /* @__PURE__ */ m("button", {
			type: "button",
			onClick: () => {
				o(e);
			},
			className: `${ve(!0)} inline-flex items-center gap-1`,
			title: "Not a recognized value — click to remove",
			children: [e, /* @__PURE__ */ p(d, { className: "h-3 w-3" })]
		}, `extra:${e}`))]
	});
}
function Te({ options: e, values: t, onChange: n, addPrompt: r }) {
	let i = (t) => e.find((e) => e.value === t)?.label ?? t, a = fe(e, t);
	function o(e, r) {
		let i = e + r;
		if (i < 0 || i >= t.length) return;
		let a = [...t];
		[a[e], a[i]] = [a[i], a[e]], n(a);
	}
	function s(e) {
		n(t.filter((t, n) => n !== e));
	}
	return /* @__PURE__ */ m("div", { children: [t.length > 0 ? /* @__PURE__ */ p("div", {
		className: "mb-1 flex flex-wrap gap-1",
		children: t.map((e, t) => /* @__PURE__ */ m("span", {
			className: "inline-flex items-center gap-1 rounded-lg border border-border bg-card px-2 py-0.5 text-xs text-foreground",
			children: [
				/* @__PURE__ */ p("button", {
					type: "button",
					"aria-label": `Move ${i(e)} up`,
					onClick: () => {
						o(t, -1);
					},
					className: "text-muted hover:text-foreground",
					children: "↑"
				}),
				/* @__PURE__ */ p("button", {
					type: "button",
					"aria-label": `Move ${i(e)} down`,
					onClick: () => {
						o(t, 1);
					},
					className: "text-muted hover:text-foreground",
					children: "↓"
				}),
				/* @__PURE__ */ p("span", { children: i(e) }),
				/* @__PURE__ */ p("button", {
					type: "button",
					"aria-label": `Remove ${i(e)}`,
					onClick: () => {
						s(t);
					},
					className: "text-muted hover:text-foreground",
					children: /* @__PURE__ */ p(d, { className: "h-3 w-3" })
				})
			]
		}, e))
	}) : null, a.length > 0 ? /* @__PURE__ */ m("select", {
		value: "",
		onChange: (e) => {
			let r = e.target.value;
			r !== "" && n([...t, r]);
		},
		className: I,
		children: [/* @__PURE__ */ p("option", {
			value: "",
			children: r
		}), a.map((e) => /* @__PURE__ */ p("option", {
			value: e.value,
			children: e.label
		}, e.value))]
	}) : null] });
}
function Ee({ tokens: e, values: t, onAdd: n }) {
	return /* @__PURE__ */ m("div", {
		className: "mt-1",
		children: [/* @__PURE__ */ p("span", {
			className: "mb-1 block text-xs text-muted",
			children: "Add a token:"
		}), /* @__PURE__ */ p("div", {
			className: "flex flex-wrap gap-1",
			children: e.map((e) => t.includes(e) ? /* @__PURE__ */ p("button", {
				type: "button",
				disabled: !0,
				className: `${he} border-border bg-card text-muted font-mono`,
				children: e
			}, e) : /* @__PURE__ */ p(L, {
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
function De({ rows: e, onChange: n, makeRow: r, renderRow: o, addLabel: l, ordered: u = !1 }) {
	let [f, h] = a(() => e.map((e, t) => t)), g = i(e.length);
	t(() => {
		f.length !== e.length && (g.current = e.length, h(e.map((e, t) => t)));
	}, [e, f.length]);
	function _(t, r) {
		n(e.map((e, n) => n === t ? {
			...e,
			...r
		} : e));
	}
	function v(t) {
		n(e.filter((e, n) => n !== t)), h((e) => e.filter((e, n) => n !== t));
	}
	function y(t, r) {
		let i = t + r;
		if (i < 0 || i >= e.length) return;
		let a = [...e];
		[a[t], a[i]] = [a[i], a[t]], n(a), h((e) => {
			let n = [...e];
			return [n[t], n[i]] = [n[i], n[t]], n;
		});
	}
	function b() {
		n([...e, r()]), h((e) => [...e, g.current++]);
	}
	return /* @__PURE__ */ m("div", {
		className: "space-y-2",
		children: [e.map((t, n) => /* @__PURE__ */ m("div", {
			className: "flex items-start gap-2 rounded-xl border border-border bg-card p-3",
			children: [
				/* @__PURE__ */ p("div", {
					className: "min-w-0 flex-1 space-y-2",
					children: o(t, n, (e) => {
						_(n, e);
					})
				}),
				u ? /* @__PURE__ */ m("span", {
					className: "flex flex-col text-muted",
					children: [/* @__PURE__ */ p("button", {
						type: "button",
						"aria-label": `Move row ${n + 1} up`,
						onClick: () => {
							y(n, -1);
						},
						className: "hover:text-foreground",
						children: /* @__PURE__ */ p(c, { className: "h-4 w-4" })
					}), /* @__PURE__ */ p("button", {
						type: "button",
						"aria-label": `Move row ${n + 1} down`,
						onClick: () => {
							y(n, 1);
						},
						className: "hover:text-foreground",
						children: /* @__PURE__ */ p(s, { className: "h-4 w-4" })
					})]
				}) : null,
				/* @__PURE__ */ p("button", {
					type: "button",
					"aria-label": `Remove row ${n + 1}`,
					onClick: () => {
						v(n);
					},
					className: "text-muted hover:text-foreground",
					children: /* @__PURE__ */ p(d, { className: "h-4 w-4" })
				})
			]
		}, f.length === e.length ? f[n] : n)), /* @__PURE__ */ p(G, {
			variant: "ghost",
			onClick: b,
			children: l
		})]
	});
}
function Oe({ map: e, onChange: t, renderKey: n, renderValue: r, renderKeyLabel: i, addLabel: o }) {
	let [s, c] = a(""), [l, u] = a(""), f = Object.keys(e);
	function h(n, r) {
		t({
			...e,
			[n]: r
		});
	}
	function g(n) {
		t(Object.fromEntries(Object.entries(e).filter(([e]) => e !== n)));
	}
	function _() {
		let n = s.trim();
		n.length === 0 || n in e || (t({
			...e,
			[n]: l
		}), c(""), u(""));
	}
	let v = s.trim().length > 0 && s.trim() in e;
	return /* @__PURE__ */ m("div", {
		className: "space-y-2",
		children: [
			f.map((t) => /* @__PURE__ */ m("div", {
				className: "flex items-center gap-2 rounded-xl border border-border bg-card p-3",
				children: [
					/* @__PURE__ */ p("span", {
						className: "min-w-0 flex-1 truncate font-mono text-sm text-foreground",
						children: i ? i(t) : t
					}),
					/* @__PURE__ */ p("span", {
						className: "flex-1",
						children: r(e[t], (e) => {
							h(t, e);
						})
					}),
					/* @__PURE__ */ p("button", {
						type: "button",
						"aria-label": `Remove ${t}`,
						onClick: () => {
							g(t);
						},
						className: "text-muted hover:text-foreground",
						children: /* @__PURE__ */ p(d, { className: "h-4 w-4" })
					})
				]
			}, t)),
			/* @__PURE__ */ m("div", {
				className: "flex items-start gap-2 rounded-xl border border-border bg-card p-3",
				children: [
					/* @__PURE__ */ p("span", {
						className: "min-w-0 flex-1",
						children: n(s, c, f)
					}),
					/* @__PURE__ */ p("span", {
						className: "min-w-0 flex-1",
						children: r(l, u)
					}),
					/* @__PURE__ */ p(G, {
						onClick: _,
						disabled: s.trim().length === 0 || v,
						children: o
					})
				]
			}),
			v ? /* @__PURE__ */ p(K, {
				kind: "error",
				children: "That key already has a value."
			}) : null
		]
	});
}
function ke({ pattern: e, isRegex: t }) {
	if (!t) return null;
	let n = se(e);
	return n.valid ? null : /* @__PURE__ */ m(K, {
		kind: "error",
		children: ["Invalid pattern: ", n.message]
	});
}
function Ae({ value: e }) {
	return e.trim().length === 0 || ce(e) ? null : /* @__PURE__ */ p(K, {
		kind: "warning",
		children: "Doesn't look like an absolute path."
	});
}
function U({ title: e, description: t, headerRight: n, children: r }) {
	return /* @__PURE__ */ m("div", {
		className: "rounded-xl border border-border bg-card p-4",
		children: [
			n ? /* @__PURE__ */ m("div", {
				className: "flex items-center justify-between gap-4",
				children: [/* @__PURE__ */ p("h3", {
					className: "text-base font-semibold text-foreground",
					children: e
				}), n]
			}) : /* @__PURE__ */ p("h3", {
				className: "text-base font-semibold text-foreground",
				children: e
			}),
			t ? /* @__PURE__ */ p("p", {
				className: "mb-4 mt-1 text-sm text-secondary",
				children: t
			}) : /* @__PURE__ */ p("div", { className: "mb-4" }),
			/* @__PURE__ */ p("div", {
				className: "space-y-4",
				children: r
			})
		]
	});
}
function je({ children: e, mono: t = !1 }) {
	return /* @__PURE__ */ p("span", {
		className: `inline-flex items-center rounded-md px-2 py-0.5 text-xs font-semibold border border-accent/40 bg-accent/15 text-accent ${t ? "font-mono" : "uppercase tracking-wider"}`,
		children: e
	});
}
function Me({ title: e, hint: t }) {
	return /* @__PURE__ */ m("div", {
		className: "flex items-center gap-3",
		children: [
			/* @__PURE__ */ p("h2", {
				className: "text-xs font-bold uppercase tracking-wider text-secondary",
				children: e
			}),
			/* @__PURE__ */ p("div", { className: "h-px flex-1 bg-border" }),
			t ? /* @__PURE__ */ p("span", {
				className: "text-xs text-muted",
				children: t
			}) : null
		]
	});
}
function W({ title: e, description: t, badge: n, headerRight: r, accent: i = !1, children: a }) {
	let o = i ? "border-accent/30" : "border-border", s = !!e || n != null || r != null;
	return /* @__PURE__ */ m("section", {
		className: `overflow-hidden rounded-2xl border ${o} bg-surface shadow-sm`,
		children: [s ? /* @__PURE__ */ m("div", {
			className: "flex items-start gap-3 border-b border-border px-5 py-4",
			children: [
				n ? /* @__PURE__ */ p("span", {
					className: "mt-0.5",
					children: n
				}) : null,
				/* @__PURE__ */ m("div", {
					className: "min-w-0 flex-1",
					children: [e ? /* @__PURE__ */ p("h3", {
						className: "text-base font-semibold text-foreground",
						children: e
					}) : null, t ? /* @__PURE__ */ p("p", {
						className: "mt-1 text-sm text-secondary",
						children: t
					}) : null]
				}),
				r ? /* @__PURE__ */ p("div", {
					className: "shrink-0",
					children: r
				}) : null
			]
		}) : null, /* @__PURE__ */ p("div", {
			className: "space-y-4 p-5",
			children: a
		})]
	});
}
function Ne({ title: e, description: t, enabled: n, onToggle: r, children: i }) {
	return /* @__PURE__ */ m("section", {
		className: "overflow-hidden rounded-2xl border border-border bg-surface shadow-sm",
		children: [/* @__PURE__ */ m("div", {
			className: "flex items-center gap-3 px-5 py-4",
			children: [/* @__PURE__ */ m("div", {
				className: "min-w-0 flex-1",
				children: [/* @__PURE__ */ p("h3", {
					className: "text-base font-semibold text-foreground",
					children: e
				}), t ? /* @__PURE__ */ p("p", {
					className: "mt-1 text-sm text-secondary",
					children: t
				}) : null]
			}), /* @__PURE__ */ p("div", {
				className: "shrink-0",
				children: /* @__PURE__ */ p(H, {
					label: "",
					ariaLabel: `Enable ${e}`,
					checked: n,
					onChange: r
				})
			})]
		}), n ? /* @__PURE__ */ p("div", {
			className: "space-y-4 border-t border-border px-5 pb-5 pt-4",
			children: i
		}) : null]
	});
}
function Pe({ title: e, summary: t, defaultOpen: n = !1, children: r }) {
	let [i, o] = a(n);
	return /* @__PURE__ */ m("div", {
		className: "overflow-hidden rounded-xl border border-border",
		children: [/* @__PURE__ */ m("button", {
			type: "button",
			onClick: () => {
				o((e) => !e);
			},
			"aria-expanded": i,
			className: "flex w-full items-center justify-between gap-4 bg-card px-4 py-3 text-left transition-colors hover:bg-card-hover",
			children: [/* @__PURE__ */ m("span", {
				className: "min-w-0",
				children: [/* @__PURE__ */ p("span", {
					className: "block text-sm font-medium text-foreground",
					children: e
				}), t ? /* @__PURE__ */ p("span", {
					className: "mt-1 block truncate text-xs text-muted",
					children: t
				}) : null]
			}), p(i ? c : s, { className: "h-4 w-4 shrink-0 text-muted" })]
		}), i ? /* @__PURE__ */ p("div", {
			className: "space-y-4 border-t border-border px-4 py-4",
			children: r
		}) : null]
	});
}
function G({ variant: e = "primary", children: t, onClick: n, disabled: r }) {
	return /* @__PURE__ */ p("button", {
		type: "button",
		onClick: n,
		disabled: r,
		className: e === "ghost" ? "inline-flex items-center gap-1.5 rounded-lg border border-border bg-card px-3 py-2 text-sm font-medium text-secondary hover:border-accent/50 hover:bg-card-hover hover:text-foreground disabled:opacity-60" : "inline-flex items-center gap-2 rounded-lg bg-accent px-4 py-2 text-sm font-medium text-white hover:bg-accent-hover disabled:opacity-60",
		children: t
	});
}
function K({ kind: e, children: t }) {
	return /* @__PURE__ */ p("span", {
		className: `text-xs ${e === "success" ? "text-green-400" : e === "error" ? "text-red-400" : e === "warning" ? "text-amber-400" : "text-secondary"}`,
		children: t
	});
}
function q() {
	return /* @__PURE__ */ p(l, { className: "h-4 w-4 animate-spin" });
}
//#endregion
//#region src/entityPicker.tsx
var J = "com.alextomas955.renamer", Y = `/extensions/${J}/list-studios`, X = `/extensions/${J}/list-tags`, Fe = `/extensions/${J}/list-performers`, Ie = "w-full rounded-xl border border-border bg-card px-3 py-2 text-sm text-foreground focus:border-accent focus:outline-none", Le = "cursor-pointer rounded-lg px-2 py-1 text-left text-sm text-foreground hover:bg-card-hover", Z = "inline-flex items-center gap-1 rounded-lg border border-border bg-card px-2 py-0.5 text-xs text-foreground", Q = "border-red-400 text-red-400";
function Re({ label: r, helper: o, values: s, onChange: c, endpointPath: l, adapter: u, placeholder: f, excludeValues: h }) {
	let g = n(), [v, y] = a(""), [b, x] = a(!1), [S, C] = a([]), [w, T] = a(!1), [E, D] = a(!1), [O, k] = a(!1), A = i(!1), ee = i(null);
	t(() => {
		if (!b) return;
		let e = (e) => {
			ee.current?.contains(e.target) || x(!1);
		};
		return document.addEventListener("mousedown", e), () => {
			document.removeEventListener("mousedown", e);
		};
	}, [b]);
	let te = e(async () => {
		if (!(A.current || w)) {
			A.current = !0, D(!0);
			try {
				let e = await _(l);
				C(e), T(!0), k(!1);
			} catch {
				k(!0);
			} finally {
				A.current = !1, D(!1);
			}
		}
	}, [l, w]);
	function j() {
		x(!0), te();
	}
	function M(e) {
		let t = u.toValue(e, S);
		s.includes(t) || c([...s, t]), y(""), x(!1);
	}
	function ne(e) {
		c(s.filter((t) => t !== e));
	}
	let N = ue(v, de(S, h ? [...s, ...h] : s, u.valueOf));
	return /* @__PURE__ */ m(z, {
		label: r,
		helper: o,
		children: [
			s.length > 0 ? /* @__PURE__ */ p("div", {
				className: "mb-1 flex flex-wrap gap-1",
				children: s.map((e) => /* @__PURE__ */ m("span", {
					className: w && !u.isResolved(e, S) ? `${Z} ${Q}` : Z,
					children: [/* @__PURE__ */ p("span", { children: u.toLabel(e, S) }), /* @__PURE__ */ p("button", {
						type: "button",
						"aria-label": `Remove ${u.toLabel(e, S)}`,
						onClick: () => {
							ne(e);
						},
						className: "text-muted hover:text-foreground",
						children: /* @__PURE__ */ p(d, { className: "h-3 w-3" })
					})]
				}, String(e)))
			}) : null,
			/* @__PURE__ */ m("div", {
				className: "relative",
				ref: ee,
				children: [/* @__PURE__ */ p("input", {
					id: g,
					type: "text",
					value: v,
					placeholder: f,
					className: Ie,
					onFocus: j,
					onChange: (e) => {
						y(e.target.value), x(!0);
					},
					onKeyDown: (e) => {
						e.key === "Enter" ? (e.preventDefault(), v.trim() !== "" && N.length > 0 && M(N[0])) : e.key === "Escape" && x(!1);
					}
				}), b && !O ? /* @__PURE__ */ p("div", {
					className: "mt-1 flex max-h-48 flex-col gap-0.5 overflow-auto rounded-xl border border-border bg-card p-1",
					children: E ? /* @__PURE__ */ m("span", {
						className: "flex items-center gap-2 px-2 py-1 text-sm text-muted",
						children: [/* @__PURE__ */ p(q, {}), "Loading…"]
					}) : N.length === 0 ? /* @__PURE__ */ p("span", {
						className: "px-2 py-1 text-sm text-muted",
						children: "No matches"
					}) : N.map((e) => /* @__PURE__ */ p("button", {
						type: "button",
						className: Le,
						onClick: () => {
							M(e);
						},
						children: e.name
					}, e.id))
				}) : null]
			}),
			O ? /* @__PURE__ */ p("span", {
				className: "mt-1 block",
				children: /* @__PURE__ */ p(K, {
					kind: "error",
					children: "Could not load the list — existing values stay editable."
				})
			}) : null
		]
	});
}
var ze = {
	toValue: (e) => e.id,
	valueOf: (e) => e.id,
	toLabel: (e, t) => pe(e, t),
	isResolved: (e, t) => me(e, t)
}, Be = {
	toValue: (e, t) => F(e.name, t),
	valueOf: (e) => e.name,
	toLabel: (e) => e,
	isResolved: (e, t) => t.some((t) => t.name.toLowerCase() === e.toLowerCase())
}, Ve = {
	toValue: (e, t) => F(e.name, t),
	valueOf: (e) => e.name,
	toLabel: (e) => e,
	isResolved: (e, t) => t.some((t) => t.name.toLowerCase() === e.toLowerCase())
};
function He({ label: e, helper: t, values: n, onChange: r, placeholder: i, excludeValues: a }) {
	return /* @__PURE__ */ p(Re, {
		label: e,
		helper: t,
		values: n,
		onChange: r,
		endpointPath: Y,
		adapter: ze,
		placeholder: i,
		excludeValues: a
	});
}
function Ue({ label: e, helper: t, values: n, onChange: r, placeholder: i, excludeValues: a }) {
	return /* @__PURE__ */ p(Re, {
		label: e,
		helper: t,
		values: n,
		onChange: r,
		endpointPath: X,
		adapter: Be,
		placeholder: i,
		excludeValues: a
	});
}
function We({ label: e, helper: t, values: n, onChange: r, placeholder: i, excludeValues: a }) {
	return /* @__PURE__ */ p(Re, {
		label: e,
		helper: t,
		values: n,
		onChange: r,
		endpointPath: Fe,
		adapter: Ve,
		placeholder: i,
		excludeValues: a
	});
}
//#endregion
//#region src/studioMapLogic.ts
function Ge(e) {
	let t = {};
	for (let [n, r] of Object.entries(e)) t[n] = r;
	return t;
}
function Ke(e) {
	let t = {};
	for (let [n, r] of Object.entries(e)) {
		let e = Number(n);
		Number.isInteger(e) && typeof r == "string" && (t[e] = r);
	}
	return t;
}
//#endregion
//#region src/studioMap.tsx
var qe = "/extensions/com.alextomas955.renamer/list-studios";
function Je({ map: e, onChange: n }) {
	let [r, i] = a([]);
	return t(() => {
		let e = !0;
		return _(qe).then((t) => {
			e && i(t);
		}).catch(() => {}), () => {
			e = !1;
		};
	}, []), /* @__PURE__ */ p(Oe, {
		map: Ge(e),
		onChange: (e) => {
			n(Ke(e));
		},
		renderKey: (e, t, n) => /* @__PURE__ */ p(Ye, {
			draftKey: e,
			setDraftKey: t,
			existingKeys: n
		}),
		renderValue: (e, t) => /* @__PURE__ */ m(f, { children: [/* @__PURE__ */ p(B, {
			value: e,
			onChange: t,
			placeholder: "Destination root"
		}), /* @__PURE__ */ p(Ae, { value: e })] }),
		renderKeyLabel: (e) => pe(Number(e), r),
		addLabel: "Add studio rule"
	});
}
function Ye({ draftKey: e, setDraftKey: t, existingKeys: n }) {
	return /* @__PURE__ */ p(He, {
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
var Xe = [
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
function Ze(e) {
	return `Inserts wrapped in an optional group: ${e.insert} — disappears cleanly when empty.`;
}
function Qe({ onInsert: e }) {
	return /* @__PURE__ */ m("div", { children: [/* @__PURE__ */ m("p", {
		className: "mb-1 text-xs text-muted",
		children: [
			"Click a token to insert it. ",
			/* @__PURE__ */ p("span", {
				className: "text-foreground",
				children: "Optional tokens"
			}),
			" (marked",
			" ",
			/* @__PURE__ */ p("span", {
				className: "font-mono",
				children: "{ }"
			}),
			") insert wrapped so they vanish — with their punctuation — when empty. ",
			/* @__PURE__ */ p("span", {
				className: "text-foreground",
				children: "Core tokens"
			}),
			" insert as-is."
		]
	}), /* @__PURE__ */ p("div", {
		className: "flex flex-wrap gap-1",
		children: Xe.map((t) => /* @__PURE__ */ m(L, {
			selected: !1,
			mono: !0,
			title: t.kind === "optional" ? Ze(t) : t.label,
			onClick: () => {
				e(t.insert);
			},
			children: [t.token, t.kind === "optional" ? /* @__PURE__ */ p("span", {
				className: "ml-1 text-muted",
				children: "{ }"
			}) : null]
		}, t.token))
	})] });
}
//#endregion
//#region src/PreviewCard.tsx
function $e(e, t) {
	switch (e) {
		case "empty": return "⚠ This template produces an empty name for this sample.";
		case "sanitized": return "⚠ Adjusted: illegal characters were stripped or replaced.";
		case "length-reduced": return t.droppedFields.length > 0 ? `⚠ Shortened to fit the path limit — dropped: ${t.droppedFields.join(", ")}.` : "⚠ Shortened to fit the path limit.";
		case "gating-skip": return "⚠ Would be skipped: a required field is missing for this sample.";
		default: return null;
	}
}
function et({ result: e }) {
	return /* @__PURE__ */ m("div", {
		className: "rounded-xl border border-border bg-card p-4",
		children: [
			/* @__PURE__ */ m("div", {
				className: "mb-2 text-xs font-medium uppercase tracking-wide text-muted",
				children: ["Sample: ", e.sampleLabel]
			}),
			e.folder.length > 0 ? /* @__PURE__ */ m("div", {
				className: "mb-1 text-xs text-secondary",
				children: [e.folder.split("/").join(" / "), " /"]
			}) : null,
			/* @__PURE__ */ p("div", {
				className: "font-mono text-sm text-muted line-through",
				children: e.oldName
			}),
			/* @__PURE__ */ m("div", {
				className: "font-mono text-sm text-foreground",
				children: [/* @__PURE__ */ p("span", {
					className: "text-muted",
					children: "Renamed → "
				}), e.newName]
			}),
			e.flags.length > 0 ? /* @__PURE__ */ p("div", {
				className: "mt-2 space-y-1",
				children: e.flags.map((t) => {
					let n = $e(t, e);
					return n ? /* @__PURE__ */ p("p", {
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
var tt = "a[href], button:not([disabled]), textarea, input, select, [tabindex]:not([tabindex=\"-1\"])";
function nt({ titleId: n, describedById: r, pending: a = !1, onCancel: o, size: s = "lg", children: c }) {
	let l = i(null), u = e(() => {
		a || o();
	}, [a, o]);
	return t(() => {
		let e = document.activeElement;
		return (l.current?.querySelector(tt))?.focus(), () => e?.focus();
	}, []), t(() => {
		function e(e) {
			if (e.key === "Escape") {
				e.preventDefault(), u();
				return;
			}
			if (e.key !== "Tab") return;
			let t = l.current;
			if (!t) return;
			let n = Array.from(t.querySelectorAll(tt));
			if (n.length === 0) return;
			let r = n[0], i = n[n.length - 1], a = document.activeElement;
			e.shiftKey && a === r ? (e.preventDefault(), i.focus()) : !e.shiftKey && a === i && (e.preventDefault(), r.focus());
		}
		return document.addEventListener("keydown", e), () => {
			document.removeEventListener("keydown", e);
		};
	}, [u]), /* @__PURE__ */ m("div", {
		className: "fixed inset-0 z-50 flex items-center justify-center",
		children: [/* @__PURE__ */ p("div", {
			className: "fixed inset-0 bg-black/60",
			onClick: u,
			"aria-hidden": "true"
		}), /* @__PURE__ */ p("div", {
			ref: l,
			role: "dialog",
			"aria-modal": "true",
			"aria-labelledby": n,
			"aria-describedby": r,
			className: `relative ${s === "sm" ? "max-w-sm" : s === "xl" ? "max-w-5xl" : "max-w-2xl"} w-full mx-4 rounded-lg border border-border bg-surface p-6 shadow-xl`,
			children: c
		})]
	});
}
function rt({ children: e }) {
	return /* @__PURE__ */ p("div", {
		className: "rounded border border-red-700 bg-red-950/60 px-3 py-2 text-sm text-red-200",
		children: e
	});
}
//#endregion
//#region src/UndoSection.tsx
var it = "com.alextomas955.renamer", at = `/extensions/${it}/last-batch`, ot = `/extensions/${it}/undo`, st = "rename-undo-confirm-title", ct = "rename-undo-confirm-message", lt = 621355968e5, ut = 1e4, dt = lt * ut;
function ft(e) {
	return (e - dt) / ut;
}
function pt(e, t = Date.now()) {
	let n = t - e, r = Math.round(n / 1e3);
	if (r < 45) return "just now";
	let i = Math.round(r / 60);
	if (i < 60) return `${i} minute${i === 1 ? "" : "s"} ago`;
	let a = Math.round(i / 60);
	if (a < 24) return `${a} hour${a === 1 ? "" : "s"} ago`;
	let o = Math.round(a / 24);
	return o === 1 ? "yesterday" : o <= 7 ? `${o} days ago` : new Date(e).toLocaleDateString();
}
function mt(e) {
	return e instanceof v ? `${e.status} ${e.body}` : String(e);
}
function ht({ refreshKey: n }) {
	let [r, i] = a(null), [o, s] = a(!0), [c, l] = a(null), [d, f] = a(!1), [h, g] = a(!1), [y, b] = a(null), x = e(async () => {
		s(!0), l(null);
		try {
			let e = await _(at);
			i(e);
		} catch (e) {
			l(mt(e));
		} finally {
			s(!1);
		}
	}, []);
	t(() => {
		x();
	}, [x, n]);
	let S = !!r && r.hasBatch && !r.consumed, C = r?.count ?? 0, w = r ? ft(r.writtenAtUtcTicks) : 0;
	async function T() {
		g(!0), b(null);
		try {
			let e = await _(ot, { method: "POST" }), t = (e.failed?.length ?? 0) + (e.skipped?.length ?? 0);
			if (t === 0) b({
				kind: "success",
				text: `Undone — ${e.undone} file${e.undone === 1 ? "" : "s"} moved back to their original names.`
			});
			else if (e.undone > 0) {
				let n = e.failed?.[0]?.reason ?? e.skipped?.[0]?.reason ?? "unknown reason";
				b({
					kind: "error",
					text: `Undo finished with problems — ${t} file${t === 1 ? "" : "s"} couldn't be moved back (${n}). The rest were restored.`
				});
			} else {
				let t = e.failed?.[0]?.reason ?? e.skipped?.[0]?.reason ?? "unknown reason";
				b({
					kind: "error",
					text: `Couldn't undo — ${t}. Nothing was changed.`
				});
			}
		} catch (e) {
			if (e instanceof v) {
				b({
					kind: "error",
					text: `Couldn't undo — ${mt(e)}. Nothing was changed.`
				});
				return;
			}
			b({
				kind: "success",
				text: "Undone — your files were moved back to their original names."
			});
		} finally {
			g(!1), f(!1), x();
		}
	}
	return /* @__PURE__ */ m("div", {
		className: "rounded-xl border border-border bg-card p-4",
		children: [
			/* @__PURE__ */ p("h3", {
				className: "text-base font-semibold text-foreground",
				children: "Undo last rename"
			}),
			/* @__PURE__ */ p("p", {
				className: "mb-4 mt-1 text-sm text-secondary",
				children: "This moves every file in that batch back to its original name. It can't be undone again. Undo history is kept in this extension's stored data, so it's lost if that data is cleared."
			}),
			o ? /* @__PURE__ */ m("div", {
				className: "flex items-center gap-2 text-sm text-secondary",
				children: [/* @__PURE__ */ p(q, {}), "Checking for a recent rename…"]
			}) : c ? /* @__PURE__ */ m("div", {
				className: "space-y-2",
				children: [/* @__PURE__ */ m(K, {
					kind: "error",
					children: [
						"Couldn't check for a recent rename — ",
						c,
						"."
					]
				}), /* @__PURE__ */ p("div", { children: /* @__PURE__ */ p(G, {
					variant: "ghost",
					onClick: () => void x(),
					children: "Retry"
				}) })]
			}) : S ? /* @__PURE__ */ m("div", {
				className: "space-y-3",
				children: [/* @__PURE__ */ m("div", {
					className: "flex items-center justify-between gap-3",
					children: [/* @__PURE__ */ m("span", {
						className: "text-sm text-foreground",
						children: [
							"Last rename: ",
							C,
							" item",
							C === 1 ? "" : "s",
							" renamed · ",
							pt(w)
						]
					}), /* @__PURE__ */ m(G, {
						variant: "ghost",
						onClick: () => {
							f(!0);
						},
						disabled: h,
						children: [/* @__PURE__ */ p(u, { className: "h-4 w-4" }), "Undo last rename"]
					})]
				}), y ? /* @__PURE__ */ p(K, {
					kind: y.kind,
					children: y.text
				}) : null]
			}) : /* @__PURE__ */ m("div", {
				className: "space-y-2",
				children: [/* @__PURE__ */ p("span", {
					className: "text-sm text-secondary",
					children: "No rename to undo."
				}), y ? /* @__PURE__ */ p("div", { children: /* @__PURE__ */ p(K, {
					kind: y.kind,
					children: y.text
				}) }) : null]
			}),
			d ? /* @__PURE__ */ m(nt, {
				titleId: st,
				describedById: ct,
				pending: h,
				onCancel: () => {
					f(!1);
				},
				size: "sm",
				children: [
					/* @__PURE__ */ p("h2", {
						id: st,
						className: "mb-2 text-lg font-semibold text-foreground",
						children: "Undo last rename?"
					}),
					/* @__PURE__ */ m("p", {
						id: ct,
						className: "mb-6 text-sm text-secondary",
						children: [
							"This moves ",
							C,
							" file",
							C === 1 ? "" : "s",
							" back to their original names. This can't be undone again."
						]
					}),
					/* @__PURE__ */ m("div", {
						className: "flex justify-end gap-3",
						children: [/* @__PURE__ */ p("button", {
							type: "button",
							onClick: () => {
								f(!1);
							},
							disabled: h,
							className: "px-4 py-2 text-sm text-secondary hover:text-foreground disabled:opacity-60",
							children: "Cancel"
						}), /* @__PURE__ */ m("button", {
							type: "button",
							onClick: () => void T(),
							disabled: h,
							className: "inline-flex items-center gap-2 rounded-md bg-red-600 px-4 py-2 text-sm font-medium text-white hover:bg-red-500 disabled:opacity-60",
							children: [
								h ? /* @__PURE__ */ p(q, {}) : null,
								"Undo ",
								C,
								" rename",
								C === 1 ? "" : "s"
							]
						})]
					})
				]
			}) : null
		]
	});
}
//#endregion
//#region src/WarningBadge.tsx
var gt = {
	amber: "border-amber-400/40 bg-amber-400/10 text-amber-400",
	gray: "border-border bg-card text-muted",
	red: "border-red-700/50 bg-red-950/40 text-red-400"
};
function _t(e) {
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
function vt({ badge: e }) {
	let t = e.variant === "amber" || e.variant === "red";
	return /* @__PURE__ */ m("span", {
		className: `inline-flex items-center gap-1 rounded-full border px-2 py-0.5 text-xs font-medium ${gt[e.variant]}`,
		children: [t ? /* @__PURE__ */ p(o, { className: "h-3 w-3" }) : null, e.label]
	});
}
function yt({ item: e }) {
	let t = _t(e);
	return t.length === 0 ? null : /* @__PURE__ */ p("span", {
		className: "inline-flex flex-wrap gap-1",
		children: t.map((e) => /* @__PURE__ */ p(vt, { badge: e }, e.label))
	});
}
//#endregion
//#region src/dryRunLogic.ts
var bt = /* @__PURE__ */ new Set([
	"SkipGated",
	"SkipCollision",
	"SkipLocked",
	"SkipBlocked",
	"SkipNoSpace",
	"SkipExcluded",
	"Failed"
]);
function xt(e) {
	let t = 0, n = 0;
	for (let r of e) r.status === "Renamer" || r.status === "Move" ? t++ : bt.has(r.status) && n++;
	return {
		renamed: t,
		skipped: n,
		scanned: e.length
	};
}
function St(e, t, n = 50) {
	return e.slice(t * n, t * n + n);
}
function Ct(e, t = 50) {
	return Math.max(1, Math.ceil(e / t));
}
//#endregion
//#region src/DryRunModal.tsx
var wt = { borderCollapse: "collapse" }, Tt = { maxWidth: 0 }, Et = "com.alextomas955.renamer", Dt = `/extensions/${Et}/scan-library`, Ot = `/extensions/${Et}/last-scan`, kt = "rename-dry-run-title", At = "rename-dry-run-summary", jt = 50, Mt = 1e3;
function Nt(e) {
	return e instanceof v ? `${e.status} ${e.body}` : String(e);
}
function Pt(e) {
	if (!e) return e;
	let t = Math.max(e.lastIndexOf("/"), e.lastIndexOf("\\"));
	return t >= 0 ? e.slice(t + 1) : e;
}
function Ft(e, n) {
	t(() => {
		if (!e) return;
		let t = !1, r = setInterval(() => {
			_(`/jobs/${e}`).then((e) => {
				t || (e.status === "completed" || e.status === "failed" || e.status === "cancelled") && (clearInterval(r), n(e));
			}).catch(() => {});
		}, Mt);
		return () => {
			t = !0, clearInterval(r);
		};
	}, [e]);
}
function It({ options: e, onClose: n, onRenameAll: r, renaming: o }) {
	let [s, c] = a(null), [l, u] = a(null), [d, h] = a(null), [g, v] = a(0), y = i(!1);
	t(() => {
		y.current || (y.current = !0, _(Dt, {
			method: "POST",
			body: JSON.stringify({ Options: JSON.stringify(e) })
		}).then((e) => {
			c(e.jobId);
		}).catch((e) => {
			h(Nt(e));
		}));
	}, []), Ft(s, (e) => {
		if (e.status !== "completed") {
			h(e.error ?? "the scan job did not complete");
			return;
		}
		_(Ot).then((e) => {
			u(e);
		}).catch((e) => {
			h(Nt(e));
		});
	});
	let b = l ? xt(l) : null, x = l ? Ct(l.length, jt) : 1, S = l ? St(l, g, jt) : [];
	return /* @__PURE__ */ m(nt, {
		titleId: kt,
		describedById: At,
		pending: o,
		onCancel: n,
		size: "xl",
		children: [
			/* @__PURE__ */ p("h2", {
				id: kt,
				className: "mb-2 text-lg font-semibold text-foreground",
				children: "Dry run"
			}),
			d ? /* @__PURE__ */ p("div", {
				className: "mb-4",
				children: /* @__PURE__ */ m(rt, { children: [
					"Couldn't scan your library — ",
					d,
					". Close and try again."
				] })
			}) : l === null || b === null ? /* @__PURE__ */ m("div", {
				className: "flex items-center gap-2 py-8 text-sm text-secondary",
				children: [/* @__PURE__ */ p(q, {}), "Scanning your library…"]
			}) : /* @__PURE__ */ m(f, { children: [/* @__PURE__ */ m("p", {
				id: At,
				className: "mb-4 text-sm text-secondary",
				children: [
					/* @__PURE__ */ p("span", {
						className: "text-foreground",
						children: b.renamed
					}),
					" will be renamed ·",
					" ",
					b.skipped,
					" skipped · ",
					b.scanned,
					" scanned"
				]
			}), b.scanned === 0 ? /* @__PURE__ */ p("p", {
				className: "py-8 text-center text-sm text-secondary",
				children: "No items match your current settings — nothing to rename."
			}) : /* @__PURE__ */ p(f, { children: /* @__PURE__ */ m("div", {
				className: "max-h-96 overflow-y-auto rounded border border-border text-sm",
				children: [/* @__PURE__ */ m("table", {
					className: "w-full",
					style: wt,
					children: [/* @__PURE__ */ p("thead", { children: /* @__PURE__ */ m("tr", {
						className: "sticky top-0 bg-card text-left",
						children: [
							/* @__PURE__ */ p("th", {
								className: "w-20 px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted",
								children: "Type"
							}),
							/* @__PURE__ */ p("th", {
								className: "min-w-0 flex-1 px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted",
								children: "Current name"
							}),
							/* @__PURE__ */ p("th", {
								className: "min-w-0 flex-1 px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted",
								children: "New name"
							}),
							/* @__PURE__ */ p("th", {
								className: "min-w-0 flex-1 px-3 py-2 text-xs font-medium uppercase tracking-wide text-muted",
								children: "Destination"
							}),
							/* @__PURE__ */ p("th", { className: "px-3 py-2" })
						]
					}) }), /* @__PURE__ */ p("tbody", {
						className: "divide-y divide-border",
						children: S.map((e) => {
							let t = e.status !== "Renamer" && e.status !== "Move", n = Pt(e.oldFullPath), r = e.newBasename || Pt(e.newFullPath);
							return /* @__PURE__ */ m("tr", {
								className: t ? "opacity-70" : void 0,
								children: [
									/* @__PURE__ */ p("td", {
										className: "w-20 px-3 py-2 text-sm text-secondary",
										children: e.kind
									}),
									/* @__PURE__ */ p("td", {
										className: "min-w-0 truncate px-3 py-2 font-mono text-sm text-muted",
										style: Tt,
										title: e.oldFullPath,
										children: n
									}),
									/* @__PURE__ */ p("td", {
										className: `min-w-0 truncate px-3 py-2 font-mono text-sm ${t ? "text-muted" : "text-foreground"}`,
										style: Tt,
										title: e.newFullPath,
										children: t ? "— will be skipped" : r
									}),
									/* @__PURE__ */ p("td", {
										className: "min-w-0 truncate px-3 py-2 font-mono text-xs text-muted",
										style: Tt,
										title: e.targetFolderPath,
										children: e.targetFolderPath
									}),
									/* @__PURE__ */ p("td", {
										className: "px-3 py-2",
										children: /* @__PURE__ */ p(yt, { item: e })
									})
								]
							}, e.fileId);
						})
					})]
				}), /* @__PURE__ */ m("div", {
					className: "flex items-center justify-between border-t border-border bg-card px-3 py-2",
					children: [
						/* @__PURE__ */ p(G, {
							variant: "ghost",
							onClick: () => {
								v((e) => e - 1);
							},
							disabled: g === 0,
							children: "Prev"
						}),
						/* @__PURE__ */ m("span", {
							className: "text-xs text-muted",
							children: [
								"Page ",
								g + 1,
								" of ",
								x
							]
						}),
						/* @__PURE__ */ p(G, {
							variant: "ghost",
							onClick: () => {
								v((e) => e + 1);
							},
							disabled: g === x - 1,
							children: "Next"
						})
					]
				})]
			}) })] }),
			/* @__PURE__ */ m("div", {
				className: "mt-6 flex justify-end gap-3",
				children: [/* @__PURE__ */ p(G, {
					variant: "ghost",
					onClick: n,
					disabled: o,
					children: "Close"
				}), /* @__PURE__ */ m(G, {
					onClick: () => {
						l && r(l);
					},
					disabled: o || !b || b.renamed === 0,
					children: [
						o ? /* @__PURE__ */ p(q, {}) : null,
						"Rename ",
						b?.renamed ?? 0,
						" files"
					]
				})]
			})
		]
	});
}
//#endregion
//#region src/templateValidation.ts
var Lt = new Set(Xe.map((e) => e.token.slice(1).toLowerCase())), Rt = Xe.map((e) => e.token.slice(1));
function zt(e) {
	let t = e.startsWith("$") ? e.slice(1) : e;
	return Lt.has(t.toLowerCase());
}
function Bt(e) {
	let t = 0;
	for (let n of e) if (n === "{") t++;
	else if (n === "}" && (t--, t < 0)) return !1;
	return t === 0;
}
function Vt(e) {
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
		!Lt.has(o) && !n.has(o) && (n.add(o), t.push(`$${a}`)), r = i - 1;
	}
	return t;
}
function Ht(e, t) {
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
function Ut(e, t, n) {
	let r = (e.startsWith("$") ? e.slice(1) : e).toLowerCase();
	return Ht(t, r) || Ht(n, r);
}
function Wt(e, t) {
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
function Gt(e) {
	let t = (e.startsWith("$") ? e.slice(1) : e).toLowerCase(), n, r = Infinity;
	for (let e of Lt) {
		let i = Wt(t, e);
		i < r && (r = i, n = e);
	}
	return n !== void 0 && r > 0 && r <= 2 ? `$${n}` : void 0;
}
//#endregion
//#region src/presets.ts
var Kt = [
	{
		label: "Date – Title [Height]",
		filenameTemplate: "{$date - }$title{ [$height]}"
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
], $ = "com.alextomas955.renamer", qt = "options", Jt = `/extensions/${$}/data`, Yt = `/extensions/${$}/preview-sample`, Xt = `/extensions/${$}/renamer-library`, Zt = 250, Qt = 1e3;
function $t(e) {
	let t = e.trim();
	return t.startsWith(".") && (t = t.slice(1)), t.toLowerCase();
}
var en = [
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
], tn = [{
	value: "DropAll",
	label: "Drop all when over the max"
}, {
	value: "KeepFirst",
	label: "Keep the first N"
}], nn = [
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
], rn = [{
	value: "NameAsc",
	label: "Name (A→Z)"
}, {
	value: "None",
	label: "Keep original order"
}], an = [
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
], on = [
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
})), sn = [
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
], cn = [
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
], ln = [
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
], un = [
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
function dn({ value: e, emptySamples: t = [] }) {
	let n = [];
	Bt(e) || n.push("Unmatched { or } — it'll still render, but check your groups.");
	for (let t of Vt(e)) {
		let e = Gt(t);
		n.push(e ? `${t} isn't a known token — it'll render as empty. Did you mean ${e}?` : `${t} isn't a known token — it'll render as empty.`);
	}
	for (let e of t) n.push(`This template produces an empty name for the "${e}" sample.`);
	return n.length === 0 ? null : /* @__PURE__ */ p("div", {
		className: "mt-1 space-y-1",
		role: "status",
		"aria-live": "polite",
		children: n.map((e) => /* @__PURE__ */ m("p", {
			className: "flex items-start gap-1 text-xs text-amber-400",
			children: [/* @__PURE__ */ p(o, { className: "h-3 w-3 shrink-0" }), /* @__PURE__ */ p("span", { children: e })]
		}, e))
	});
}
function fn({ values: e }) {
	let t = [];
	for (let n of e) {
		if (zt(n)) continue;
		let e = Gt(n), r = e ? e.slice(1) : void 0;
		t.push(r ? `"${n}" isn't a known token — it'll be ignored. Did you mean ${r}?` : `"${n}" isn't a known token — it'll be ignored.`);
	}
	return t.length === 0 ? null : /* @__PURE__ */ p("div", {
		className: "mt-1 space-y-1",
		role: "status",
		"aria-live": "polite",
		children: t.map((e) => /* @__PURE__ */ m("p", {
			className: "flex items-start gap-1 text-xs text-amber-400",
			children: [/* @__PURE__ */ p(o, { className: "h-3 w-3 shrink-0" }), /* @__PURE__ */ p("span", { children: e })]
		}, e))
	});
}
function pn({ onApply: e }) {
	return /* @__PURE__ */ m("div", { children: [
		/* @__PURE__ */ p("span", {
			className: "mb-1 block text-xs font-medium uppercase tracking-wide text-muted",
			children: "Presets"
		}),
		/* @__PURE__ */ p("div", {
			className: "flex flex-wrap gap-1",
			children: Kt.map((t) => /* @__PURE__ */ p(L, {
				selected: !1,
				title: t.filenameTemplate,
				onClick: () => {
					e(t.filenameTemplate);
				},
				children: t.label
			}, t.label))
		}),
		/* @__PURE__ */ p("p", {
			className: "mt-1 text-xs text-muted",
			children: "Click a preset to fill the filename template. You can edit it afterwards."
		})
	] });
}
function mn({ dirty: e, saving: t, saveError: n, savedFlash: r, canSave: i, onSave: a, onDiscard: o }) {
	return e ? /* @__PURE__ */ p("div", {
		className: "pointer-events-none fixed inset-x-0 bottom-0 z-50 flex justify-center px-4 py-4",
		children: /* @__PURE__ */ m("div", {
			className: "pointer-events-auto flex w-full max-w-3xl items-center gap-4 rounded-2xl border border-border bg-card px-5 shadow-lg",
			style: {
				paddingTop: "0.875rem",
				paddingBottom: "0.875rem"
			},
			children: [
				/* @__PURE__ */ p("span", { className: `h-2 w-2 shrink-0 rounded-full ${n ? "bg-red-400" : r ? "bg-green-400" : "bg-amber-400"}` }),
				/* @__PURE__ */ p("div", {
					className: "min-w-0 flex-1",
					children: n ? /* @__PURE__ */ m(K, {
						kind: "error",
						children: [
							"Couldn't save settings — ",
							n,
							". Your changes are still here; try Save again."
						]
					}) : r ? /* @__PURE__ */ p(K, {
						kind: "success",
						children: "Settings saved."
					}) : /* @__PURE__ */ m(f, { children: [/* @__PURE__ */ p("div", {
						className: "text-sm font-semibold text-foreground",
						children: "Unsaved changes"
					}), /* @__PURE__ */ p("div", {
						className: "mt-0.5 text-xs text-secondary",
						children: "Nothing on disk changes until you save. Running a rename requires saving first."
					})] })
				}),
				/* @__PURE__ */ m("div", {
					className: "flex shrink-0 items-center gap-3",
					children: [/* @__PURE__ */ p(G, {
						variant: "ghost",
						onClick: o,
						disabled: t,
						children: "Discard"
					}), /* @__PURE__ */ m(G, {
						onClick: a,
						disabled: !i || t,
						children: [t ? /* @__PURE__ */ p(q, {}) : null, "Save changes"]
					})]
				})
			]
		})
	}) : null;
}
async function hn(e, t) {
	let n = {
		...t,
		...e
	};
	try {
		await _(`${Jt}/${qt}`, {
			method: "PUT",
			body: JSON.stringify(JSON.stringify(n))
		});
	} catch (e) {
		if (e instanceof v) throw e;
	}
}
function gn() {
	let n = b($), [r, s] = a(() => S()), [c, l] = a(() => S()), [u, d] = a(!0), [h, g] = a(null), [y, x] = a(!1), [C, w] = a(null), [T, E] = a(!1), [D, O] = a(!1), [k, A] = a(!1), [ee, te] = a(""), j = i({}), [M, ne] = a(null), [N, re] = a(!1), ie = i(null), se = i(null), ce = i("filename"), P = JSON.stringify(r) !== JSON.stringify(c), ue = P || k, [de, fe] = a(!1), [pe, me] = a(!1), [F, I] = a(null);
	function he(e) {
		return new Promise((t, n) => {
			let r = setInterval(() => {
				_(`/jobs/${e}`).then((e) => {
					e.status === "completed" ? (clearInterval(r), t()) : (e.status === "failed" || e.status === "cancelled") && (clearInterval(r), n(Error(e.error ?? "the job did not complete")));
				}).catch(() => {});
			}, Qt);
		});
	}
	let ge = e(async (e) => {
		me(!0), I(null);
		try {
			let t = e;
			if (!t) {
				let { jobId: e } = await _(`/extensions/${$}/scan-library`, { method: "POST" });
				await he(e), t = await _(`/extensions/${$}/last-scan`);
			}
			let n = xt(t), { jobId: r } = await _(Xt, { method: "POST" });
			await he(r), fe(!1), I({
				kind: "success",
				text: `Renamed ${n.renamed} file${n.renamed === 1 ? "" : "s"}` + (n.skipped > 0 ? `, ${n.skipped} skipped` : "") + "."
			});
		} catch (e) {
			let t = e instanceof v ? `${e.status} ${e.body}` : String(e);
			I({
				kind: "error",
				text: `Couldn't rename — ${t}. Nothing was changed; you can try again.`
			});
		} finally {
			me(!1);
		}
	}, []), _e = e(async () => {
		d(!0), g(null), A(!1);
		try {
			let e = (await n.getAll())[qt];
			if (e) {
				O(!1);
				let t;
				try {
					t = JSON.parse(e);
				} catch {
					j.current = {};
					let e = S();
					s(e), l(e), A(!0);
					return;
				}
				j.current = ae(t);
				let n = oe(t), r = {
					...n,
					EnableStudioDestinations: n.EnableStudioDestinations || Object.keys(n.StudioDestinations).length > 0,
					EnableTagDestinations: n.EnableTagDestinations || Object.keys(n.TagDestinations).length > 0,
					EnableAdvancedRouting: n.EnableAdvancedRouting || n.AllowedRoots.length > 0 || n.PathDestinations.length > 0
				};
				s(r), l(r);
			} else {
				O(!0), j.current = {};
				let e = S();
				s(e), l(e);
			}
		} catch (e) {
			g(e instanceof v ? `${e.status} ${e.body}` : String(e));
		} finally {
			d(!1);
		}
	}, [n]);
	t(() => {
		_e();
	}, [_e]), t(() => {
		if (u) return;
		let e = setTimeout(() => {
			_(Yt, {
				method: "POST",
				body: JSON.stringify({ Options: r })
			}).then((e) => {
				ne(e), re(!1);
			}).catch(() => {
				re(!0);
			});
		}, Zt);
		return () => {
			clearTimeout(e);
		};
	}, [r, u]);
	async function ve() {
		x(!0), w(null);
		try {
			await hn(r, j.current), l(r), O(!1), A(!1), E(!0), setTimeout(() => {
				E(!1);
			}, 3e3);
		} catch (e) {
			w(e instanceof v ? `${e.status} ${e.body}` : String(e));
		} finally {
			x(!1);
		}
	}
	function R(e, t) {
		s((n) => ({
			...n,
			[e]: t
		}));
	}
	function J(e, t) {
		s((n) => ({
			...n,
			[e]: {
				...n[e],
				...t
			}
		}));
	}
	function Y(e) {
		let t = ce.current, n = t === "folder" ? se.current : ie.current, i = t === "folder" ? "FolderTemplate" : "FilenameTemplate", a = r[i];
		if (n && typeof n.selectionStart == "number") {
			let t = n.selectionStart, r = n.selectionEnd ?? t;
			R(i, a.slice(0, t) + e + a.slice(r)), requestAnimationFrame(() => {
				n.focus();
				let r = t + e.length;
				n.setSelectionRange(r, r);
			});
		} else R(i, a + e);
	}
	if (u) return /* @__PURE__ */ m("div", {
		className: "flex items-center gap-2 text-sm text-secondary",
		children: [/* @__PURE__ */ p(q, {}), "Loading settings…"]
	});
	if (h) return /* @__PURE__ */ m("div", {
		className: "space-y-3",
		children: [/* @__PURE__ */ m(K, {
			kind: "error",
			children: [
				"Couldn't load your saved settings — ",
				h,
				". Retry, or continue with defaults below."
			]
		}), /* @__PURE__ */ p("div", { children: /* @__PURE__ */ p(G, {
			variant: "ghost",
			onClick: () => void _e(),
			children: "Retry"
		}) })]
	});
	let X = (e) => r[e], Fe = (M ?? []).filter((e) => e.flags.includes("empty")).map((e) => e.sampleLabel), Ie = Ut("performers", r.FilenameTemplate, r.FolderTemplate), Le = Ut("tags", r.FilenameTemplate, r.FolderTemplate), Z = Ut("date", r.FilenameTemplate, r.FolderTemplate), Q = Ut("duration", r.FilenameTemplate, r.FolderTemplate);
	return /* @__PURE__ */ m("div", {
		className: "space-y-6",
		style: P ? { paddingBottom: "5rem" } : void 0,
		children: [
			/* @__PURE__ */ m("div", {
				className: "grid grid-cols-1 gap-6 lg:grid-cols-3",
				children: [/* @__PURE__ */ m("div", {
					className: "col-span-2",
					children: [k ? /* @__PURE__ */ p(K, {
						kind: "error",
						children: "Your saved settings couldn't be read and have been reset to defaults. Review the options below and save to store a clean copy."
					}) : D ? /* @__PURE__ */ p(K, {
						kind: "muted",
						children: "Using default settings — pick a preset or write a template, then save."
					}) : null, /* @__PURE__ */ m(W, {
						title: "Filename & destination",
						description: "Set how files are named and where they go — pick a preset or write your own template.",
						children: [
							/* @__PURE__ */ p(pn, { onApply: (e) => {
								R("FilenameTemplate", e);
							} }),
							/* @__PURE__ */ p(z, {
								label: "Filename template",
								children: /* @__PURE__ */ p(B, {
									value: r.FilenameTemplate,
									onChange: (e) => {
										R("FilenameTemplate", e);
									},
									onFocus: () => ce.current = "filename",
									inputRef: ie,
									mono: !0,
									placeholder: "$title"
								})
							}),
							/* @__PURE__ */ p(dn, {
								value: r.FilenameTemplate,
								emptySamples: Fe
							}),
							/* @__PURE__ */ p(Qe, { onInsert: Y }),
							/* @__PURE__ */ m("div", {
								className: "border-t border-border pt-4",
								children: [
									/* @__PURE__ */ p("div", {
										className: "text-base font-semibold text-foreground",
										children: "Where files go"
									}),
									/* @__PURE__ */ p("p", {
										className: "mb-4 mt-1 text-sm text-secondary",
										children: "Folder path template — moves files on rename."
									}),
									/* @__PURE__ */ p(z, {
										label: "Folder template",
										helper: "Blank = no folder move (rename in place). Use / for sub-folders, e.g. $studio / $year.",
										children: /* @__PURE__ */ p(B, {
											value: r.FolderTemplate,
											onChange: (e) => {
												R("FolderTemplate", e);
											},
											onFocus: () => ce.current = "folder",
											inputRef: se,
											mono: !0,
											placeholder: "$studio / $year"
										})
									}),
									/* @__PURE__ */ p(dn, { value: r.FolderTemplate })
								]
							})
						]
					})]
				}), /* @__PURE__ */ p("div", { children: /* @__PURE__ */ m("div", {
					className: "space-y-4 rounded-2xl border border-border bg-surface p-5 shadow-sm lg:sticky lg:top-16",
					children: [
						/* @__PURE__ */ p("div", {
							className: "text-base font-semibold text-foreground",
							children: "Live preview"
						}),
						/* @__PURE__ */ p("p", {
							className: "mb-4 mt-1 text-sm text-secondary",
							children: "Old → new for sample items, before anything touches disk."
						}),
						N ? /* @__PURE__ */ p(K, {
							kind: "error",
							children: "Preview unavailable — saved naming still works."
						}) : null,
						M == null ? /* @__PURE__ */ m("div", {
							className: "flex items-center gap-2 text-sm text-secondary",
							children: [/* @__PURE__ */ p(q, {}), "Rendering preview…"]
						}) : /* @__PURE__ */ p("div", {
							className: "space-y-3",
							children: M.map((e) => /* @__PURE__ */ p(et, { result: e }, e.sampleLabel))
						})
					]
				}) })]
			}),
			/* @__PURE__ */ p(Me, {
				title: "What gets renamed",
				hint: "Scope and required fields"
			}),
			/* @__PURE__ */ m(W, { children: [
				/* @__PURE__ */ p(H, {
					label: "Only rename organized items",
					checked: r.OnlyOrganized,
					onChange: (e) => {
						R("OnlyOrganized", e);
					},
					helper: "Only rename items you've marked Organized — skips un-curated items so they don't get junk names."
				}),
				/* @__PURE__ */ p(H, {
					label: "Use filename as title when none is set",
					checked: r.FilenameAsTitle,
					onChange: (e) => {
						R("FilenameAsTitle", e);
					},
					helper: "When an item has no title, use its current filename (without extension) as the title."
				}),
				/* @__PURE__ */ m(z, {
					label: "Required fields",
					helper: "Items whose listed tokens resolve to nothing are skipped instead of renamed. Default: title.",
					children: [
						/* @__PURE__ */ p(Ce, {
							values: r.RequiredFields,
							onChange: (e) => {
								R("RequiredFields", e);
							},
							placeholder: "Add token, press Enter"
						}),
						/* @__PURE__ */ p(Ee, {
							tokens: Rt,
							values: r.RequiredFields,
							onAdd: (e) => {
								R("RequiredFields", r.RequiredFields.includes(e) ? r.RequiredFields : [...r.RequiredFields, e]);
							}
						}),
						/* @__PURE__ */ p(fn, { values: r.RequiredFields })
					]
				})
			] }),
			/* @__PURE__ */ p(Me, {
				title: "Run & automation",
				hint: "When renames happen"
			}),
			/* @__PURE__ */ m(W, { children: [/* @__PURE__ */ p(H, {
				label: "Auto-rename on update",
				checked: r.AutoRenamerOnUpdate,
				onChange: (e) => {
					R("AutoRenamerOnUpdate", e);
				},
				helper: "When on, renames a video or image automatically whenever its metadata changes — respects every gating and routing rule below. The core of a hands-off library. Off by default."
			}), /* @__PURE__ */ m("div", {
				className: "border-t border-border pt-4",
				children: [
					/* @__PURE__ */ m("div", {
						className: "flex flex-wrap items-start gap-4",
						children: [/* @__PURE__ */ m("div", {
							className: "min-w-0 flex-1",
							children: [/* @__PURE__ */ p("div", {
								className: "text-base font-semibold text-foreground",
								children: "Run for the whole library"
							}), /* @__PURE__ */ p("p", {
								className: "mt-1 text-sm text-secondary",
								children: "Apply your rules to every matching item now. Preview first with a dry run — it writes nothing."
							})]
						}), /* @__PURE__ */ m("div", {
							className: "flex shrink-0 flex-wrap items-center gap-3",
							children: [/* @__PURE__ */ p(G, {
								variant: "ghost",
								onClick: () => {
									fe(!0);
								},
								children: "Dry run"
							}), /* @__PURE__ */ m(G, {
								onClick: () => void ge(),
								disabled: P || pe,
								children: [pe ? /* @__PURE__ */ p(q, {}) : null, "Rename all files"]
							})]
						})]
					}),
					P ? /* @__PURE__ */ m("p", {
						className: "mt-2 flex items-start gap-1 text-xs text-amber-400",
						role: "status",
						"aria-live": "polite",
						children: [/* @__PURE__ */ p(o, { className: "h-3 w-3 shrink-0" }), /* @__PURE__ */ p("span", { children: "Dry run previews your edits before saving. Save before “Rename all files” to run them for real." })]
					}) : null,
					F ? /* @__PURE__ */ p("p", {
						className: "mt-2",
						children: /* @__PURE__ */ m(K, {
							kind: F.kind,
							children: [
								F.kind === "success" ? "✓ " : "",
								F.text,
								F.kind === "success" ? /* @__PURE__ */ m(f, { children: [" ", /* @__PURE__ */ p("button", {
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
			de ? /* @__PURE__ */ p(It, {
				options: r,
				onClose: () => {
					fe(!1);
				},
				onRenameAll: (e) => void ge(e),
				renaming: pe
			}) : null,
			/* @__PURE__ */ p(Me, {
				title: "Token settings",
				hint: "Appear only for tokens you're using"
			}),
			/* @__PURE__ */ m("div", {
				className: "space-y-4",
				children: [
					Ie ? /* @__PURE__ */ m(W, {
						title: "Performers",
						badge: /* @__PURE__ */ p(je, {
							mono: !0,
							children: "$performers"
						}),
						accent: !0,
						children: [
							/* @__PURE__ */ p(z, {
								label: "Separator",
								children: /* @__PURE__ */ p(xe, {
									value: X("Performers").Separator,
									onChange: (e) => {
										J("Performers", { Separator: e });
									},
									options: ln,
									customPlaceholder: "Custom separator"
								})
							}),
							/* @__PURE__ */ p(z, {
								label: "Max count",
								helper: "0 = unlimited",
								children: /* @__PURE__ */ p(ye, {
									value: X("Performers").MaxCount,
									min: 0,
									onChange: (e) => {
										J("Performers", { MaxCount: e });
									}
								})
							}),
							/* @__PURE__ */ p(z, {
								label: "On overflow",
								children: /* @__PURE__ */ p(V, {
									value: X("Performers").OnOverflow,
									onChange: (e) => {
										J("Performers", { OnOverflow: e });
									},
									options: tn
								})
							}),
							/* @__PURE__ */ p(z, {
								label: "Sort",
								helper: "The id and favorite orders apply to performers only.",
								children: /* @__PURE__ */ p(V, {
									value: X("Performers").Sort,
									onChange: (e) => {
										J("Performers", { Sort: e });
									},
									options: nn
								})
							}),
							/* @__PURE__ */ p(z, {
								label: "Ignore genders",
								helper: "Drop performers of these genders before the max-count limit. A performer with no gender is always kept. None selected = off.",
								children: /* @__PURE__ */ p(we, {
									options: an,
									values: X("Performers").IgnoreGenders,
									onChange: (e) => {
										J("Performers", { IgnoreGenders: e });
									}
								})
							}),
							/* @__PURE__ */ p(z, {
								label: "Gender order",
								helper: "Preferred gender order, most-preferred first. Empty = off.",
								children: /* @__PURE__ */ p(Te, {
									options: an,
									values: X("Performers").GenderOrder,
									onChange: (e) => {
										J("Performers", { GenderOrder: e });
									},
									addPrompt: "Add a gender…"
								})
							}),
							/* @__PURE__ */ p(We, {
								label: "Whitelist",
								helper: "If set, only these performers are kept (case-insensitive).",
								values: X("Performers").Whitelist,
								onChange: (e) => {
									J("Performers", { Whitelist: e });
								},
								placeholder: "Search performers…"
							}),
							/* @__PURE__ */ p(We, {
								label: "Blacklist",
								helper: "These performers are removed (case-insensitive).",
								values: X("Performers").Blacklist,
								onChange: (e) => {
									J("Performers", { Blacklist: e });
								},
								placeholder: "Search performers…"
							})
						]
					}) : null,
					Le ? /* @__PURE__ */ m(W, {
						title: "Tags",
						badge: /* @__PURE__ */ p(je, {
							mono: !0,
							children: "$tags"
						}),
						accent: !0,
						children: [
							/* @__PURE__ */ p(z, {
								label: "Separator",
								children: /* @__PURE__ */ p(xe, {
									value: X("Tags").Separator,
									onChange: (e) => {
										J("Tags", { Separator: e });
									},
									options: ln,
									customPlaceholder: "Custom separator"
								})
							}),
							/* @__PURE__ */ p(z, {
								label: "Max count",
								helper: "0 = unlimited",
								children: /* @__PURE__ */ p(ye, {
									value: X("Tags").MaxCount,
									min: 0,
									onChange: (e) => {
										J("Tags", { MaxCount: e });
									}
								})
							}),
							/* @__PURE__ */ p(z, {
								label: "On overflow",
								children: /* @__PURE__ */ p(V, {
									value: X("Tags").OnOverflow,
									onChange: (e) => {
										J("Tags", { OnOverflow: e });
									},
									options: tn
								})
							}),
							/* @__PURE__ */ p(z, {
								label: "Sort",
								children: /* @__PURE__ */ p(V, {
									value: X("Tags").Sort,
									onChange: (e) => {
										J("Tags", { Sort: e });
									},
									options: rn
								})
							}),
							/* @__PURE__ */ p(Ue, {
								label: "Whitelist",
								helper: "If set, only these tags are kept (case-insensitive).",
								values: X("Tags").Whitelist,
								onChange: (e) => {
									J("Tags", { Whitelist: e });
								},
								placeholder: "Search tags…"
							}),
							/* @__PURE__ */ p(Ue, {
								label: "Blacklist",
								helper: "These tags are removed (case-insensitive).",
								values: X("Tags").Blacklist,
								onChange: (e) => {
									J("Tags", { Blacklist: e });
								},
								placeholder: "Search tags…"
							})
						]
					}) : null,
					Z || Q ? /* @__PURE__ */ m(W, {
						accent: !0,
						badge: /* @__PURE__ */ p(je, {
							mono: !0,
							children: Z && Q ? "$date · $duration" : Z ? "$date" : "$duration"
						}),
						title: Z && Q ? "Date & duration format" : Z ? "Date format" : "Duration format",
						children: [Z ? /* @__PURE__ */ p(z, {
							label: "Date format",
							helper: "e.g. yyyy-MM-dd",
							children: /* @__PURE__ */ p(be, {
								value: r.DateFormat,
								onChange: (e) => {
									R("DateFormat", e);
								},
								options: sn,
								customPlaceholder: "yyyy-MM-dd"
							})
						}) : null, Q ? /* @__PURE__ */ p(z, {
							label: "Duration format",
							children: /* @__PURE__ */ p(be, {
								value: r.DurationFormat,
								onChange: (e) => {
									R("DurationFormat", e);
								},
								options: cn,
								customPlaceholder: "hh\\-mm\\-ss"
							})
						}) : null]
					}) : null,
					!Ie && !Le && !Z && !Q ? /* @__PURE__ */ m("div", {
						className: "rounded-xl border border-border bg-card p-6 text-center",
						children: [
							/* @__PURE__ */ p("h3", {
								className: "text-base font-semibold text-foreground",
								children: "No token-specific settings needed"
							}),
							/* @__PURE__ */ p("p", {
								className: "mx-auto mb-4 mt-1 max-w-md text-sm text-secondary",
								children: "Add $performers, $tags, $date, or $duration to your filename or folder template to configure how they're formatted."
							}),
							/* @__PURE__ */ m("div", {
								className: "flex flex-wrap justify-center gap-1",
								children: [
									/* @__PURE__ */ p(L, {
										selected: !1,
										mono: !0,
										onClick: () => {
											Y("{ - $performers}");
										},
										children: "$performers"
									}),
									/* @__PURE__ */ p(L, {
										selected: !1,
										mono: !0,
										onClick: () => {
											Y("{ - $tags}");
										},
										children: "$tags"
									}),
									/* @__PURE__ */ p(L, {
										selected: !1,
										mono: !0,
										onClick: () => {
											Y("{ - $date}");
										},
										children: "$date"
									}),
									/* @__PURE__ */ p(L, {
										selected: !1,
										mono: !0,
										onClick: () => {
											Y("{ [$duration]}");
										},
										children: "$duration"
									})
								]
							})
						]
					}) : null
				]
			}),
			/* @__PURE__ */ p(Me, {
				title: "Destination routing",
				hint: "Where renamed files land"
			}),
			/* @__PURE__ */ m("div", {
				className: "space-y-4",
				children: [
					/* @__PURE__ */ m(U, {
						title: "Default & unorganized destinations",
						children: [/* @__PURE__ */ m("div", {
							className: "grid grid-cols-1 gap-4 md:grid-cols-2",
							children: [/* @__PURE__ */ m(z, {
								label: "Default destination",
								helper: "Where an item matching no rule goes. Blank = no default route. Honored only with the relocate gate below ON.",
								children: [/* @__PURE__ */ p(B, {
									value: r.DefaultDestination,
									onChange: (e) => {
										R("DefaultDestination", e);
									},
									placeholder: "Absolute root, or blank"
								}), /* @__PURE__ */ p(Ae, { value: r.DefaultDestination })]
							}), /* @__PURE__ */ m(z, {
								label: "Unorganized destination",
								helper: "Where un-curated items route instead of being skipped. Blank = no unorganized route.",
								children: [/* @__PURE__ */ p(B, {
									value: r.UnorganizedDestination,
									onChange: (e) => {
										R("UnorganizedDestination", e);
									},
									placeholder: "Absolute root, or blank"
								}), /* @__PURE__ */ p(Ae, { value: r.UnorganizedDestination })]
							})]
						}), /* @__PURE__ */ p(H, {
							label: "Relocate unmatched items to the default destination",
							checked: r.EnableDefaultRelocate,
							onChange: (e) => {
								R("EnableDefaultRelocate", e);
							},
							helper: "With this on, any item matching no rule is moved to the default destination — whole-library reach. Undo is the only recovery. Off by default."
						})]
					}),
					/* @__PURE__ */ p(Ne, {
						title: "Per-studio destinations",
						description: "Pick a studio, then the absolute root its items route to.",
						enabled: r.EnableStudioDestinations,
						onToggle: (e) => {
							R("EnableStudioDestinations", e);
						},
						children: /* @__PURE__ */ p(Je, {
							map: r.StudioDestinations,
							onChange: (e) => {
								R("StudioDestinations", e);
							}
						})
					}),
					/* @__PURE__ */ p(Ne, {
						title: "Per-tag destinations",
						description: "Pick a tag, then the absolute root its items route to.",
						enabled: r.EnableTagDestinations,
						onToggle: (e) => {
							R("EnableTagDestinations", e);
						},
						children: /* @__PURE__ */ p(Oe, {
							map: r.TagDestinations,
							onChange: (e) => {
								R("TagDestinations", e);
							},
							renderKey: (e, t, n) => /* @__PURE__ */ p(Ue, {
								label: "",
								values: e === "" ? [] : [e],
								onChange: (e) => {
									t(e.at(-1) ?? "");
								},
								placeholder: "Search tags…",
								excludeValues: n
							}),
							renderValue: (e, t) => /* @__PURE__ */ m(f, { children: [/* @__PURE__ */ p(B, {
								value: e,
								onChange: t,
								placeholder: "Destination root"
							}), /* @__PURE__ */ p(Ae, { value: e })] }),
							addLabel: "Add tag rule"
						})
					}),
					/* @__PURE__ */ m(Ne, {
						title: "Advanced routing & safety",
						description: "Allowed roots and source-path rules.",
						enabled: r.EnableAdvancedRouting,
						onToggle: (e) => {
							R("EnableAdvancedRouting", e);
						},
						children: [/* @__PURE__ */ m("div", { children: [
							/* @__PURE__ */ p("h4", {
								className: "text-sm font-semibold text-foreground",
								children: "Allowed roots"
							}),
							/* @__PURE__ */ p("p", {
								className: "mb-4 mt-1 text-sm text-secondary",
								children: "A rename may only write inside these absolute directories; a target outside them is rejected. Empty = files stay within their own source folder."
							}),
							/* @__PURE__ */ p(Ce, {
								values: r.AllowedRoots,
								onChange: (e) => {
									R("AllowedRoots", e);
								},
								placeholder: "Add an absolute directory, press Enter"
							})
						] }), /* @__PURE__ */ m("div", { children: [
							/* @__PURE__ */ p("h4", {
								className: "text-sm font-semibold text-foreground",
								children: "Source-path destinations"
							}),
							/* @__PURE__ */ p("p", {
								className: "mb-4 mt-1 text-sm text-secondary",
								children: "Match an item's source path to a destination root, top rule first. An exact match or a regex."
							}),
							/* @__PURE__ */ p(De, {
								rows: r.PathDestinations,
								onChange: (e) => {
									R("PathDestinations", e);
								},
								makeRow: () => ({
									Pattern: "",
									Dest: "",
									IsRegex: !1
								}),
								renderRow: (e, t, n) => /* @__PURE__ */ m(f, { children: [
									/* @__PURE__ */ p(z, {
										label: "Source path",
										children: /* @__PURE__ */ p(B, {
											value: e.Pattern,
											onChange: (e) => {
												n({ Pattern: e });
											},
											mono: !0,
											placeholder: "Exact path or regex"
										})
									}),
									/* @__PURE__ */ p(H, {
										label: "Match as a regex",
										checked: e.IsRegex,
										onChange: (e) => {
											n({ IsRegex: e });
										}
									}),
									/* @__PURE__ */ p(ke, {
										pattern: e.Pattern,
										isRegex: e.IsRegex
									}),
									/* @__PURE__ */ m(z, {
										label: "Destination root",
										children: [/* @__PURE__ */ p(B, {
											value: e.Dest,
											onChange: (e) => {
												n({ Dest: e });
											},
											placeholder: "Destination root"
										}), /* @__PURE__ */ p(Ae, { value: e.Dest })]
									})
								] }),
								addLabel: "Add path rule",
								ordered: !0
							})
						] })]
					}),
					/* @__PURE__ */ p(U, {
						title: "Sidecar files",
						description: "Files sharing the primary's basename with one of these extensions move and rename with it; a target that already exists is left untouched, never overwritten. Captions Cove tracks always move regardless.",
						children: /* @__PURE__ */ m(z, {
							label: "Also move sidecar files with these extensions",
							children: [/* @__PURE__ */ p(Ce, {
								values: r.AssociatedExtensions,
								onChange: (e) => {
									R("AssociatedExtensions", e);
								},
								placeholder: "Add an extension, press Enter",
								normalize: $t,
								onReject: (e) => !/^[a-z0-9]+$/.test(e),
								onLiveChange: (e) => {
									te(e);
								}
							}), (() => {
								let e = le($t(ee));
								return e ? /* @__PURE__ */ p(K, {
									kind: "warning",
									children: e
								}) : null;
							})()]
						})
					}),
					/* @__PURE__ */ p("div", {
						className: "rounded-xl border border-border bg-card p-4",
						children: /* @__PURE__ */ p(H, {
							label: "Delete the source folder when a move leaves it empty",
							checked: r.RemoveEmptyFolder,
							onChange: (e) => {
								R("RemoveEmptyFolder", e);
							},
							helper: "Deletes a source folder only when a move empties it completely — never a non-empty folder or a root. Undo won't move the file back into a deleted folder; the file stays at its new location. Off by default."
						})
					})
				]
			}),
			/* @__PURE__ */ p(Me, {
				title: "Advanced",
				hint: "Power-user controls — collapsed by default"
			}),
			/* @__PURE__ */ m("div", {
				className: "space-y-3",
				children: [
					/* @__PURE__ */ m(Pe, {
						title: "Clean up the name",
						summary: "Illegal-character and space handling, case, ASCII",
						children: [/* @__PURE__ */ m("div", {
							className: "grid grid-cols-1 gap-4 md:grid-cols-2",
							children: [
								/* @__PURE__ */ p(z, {
									label: "Illegal-char replacement",
									children: /* @__PURE__ */ p(Se, {
										value: r.IllegalReplacement,
										onChange: (e) => {
											R("IllegalReplacement", e);
										},
										stripLabel: "Strip",
										replaceLabel: "Replace with",
										stripHelper: "Illegal characters are removed.",
										replaceHelper: "Each illegal character becomes this.",
										inputPlaceholder: "e.g. _"
									})
								}),
								/* @__PURE__ */ p(z, {
									label: "Space replacement",
									children: /* @__PURE__ */ p(Se, {
										value: r.SpaceReplacement,
										onChange: (e) => {
											R("SpaceReplacement", e);
										},
										stripLabel: "Keep spaces",
										replaceLabel: "Replace with",
										stripHelper: "Spaces are left as-is.",
										replaceHelper: "Each space becomes this.",
										inputPlaceholder: "e.g. _ or ."
									})
								}),
								/* @__PURE__ */ p(z, {
									label: "Remove characters",
									helper: "Characters to delete from the name, e.g. ,# — separate from illegal-character handling.",
									children: /* @__PURE__ */ p(B, {
										value: r.RemoveCharacters,
										onChange: (e) => {
											R("RemoveCharacters", e);
										},
										placeholder: "e.g. ,#"
									})
								}),
								/* @__PURE__ */ p(z, {
									label: "Case",
									children: /* @__PURE__ */ p(V, {
										value: r.Case,
										onChange: (e) => {
											R("Case", e);
										},
										options: en
									})
								})
							]
						}), /* @__PURE__ */ p(H, {
							label: "ASCII transliterate",
							checked: r.AsciiTransliterate,
							onChange: (e) => {
								R("AsciiTransliterate", e);
							},
							helper: "Convert accented characters to plain ASCII."
						})]
					}),
					/* @__PURE__ */ m(Pe, {
						title: "Length & collisions",
						summary: "Length caps, what to drop when too long, duplicate suffix",
						children: [
							/* @__PURE__ */ m("div", {
								className: "grid grid-cols-1 gap-4 md:grid-cols-2",
								children: [/* @__PURE__ */ p(z, {
									label: "Filename max length",
									children: /* @__PURE__ */ p(ye, {
										value: r.FilenameMax,
										min: 1,
										onChange: (e) => {
											R("FilenameMax", e);
										}
									})
								}), /* @__PURE__ */ p(z, {
									label: "Full-path max length",
									children: /* @__PURE__ */ p(ye, {
										value: r.FullPathMax,
										min: 1,
										onChange: (e) => {
											R("FullPathMax", e);
										}
									})
								})]
							}),
							/* @__PURE__ */ m(z, {
								label: "Drop order",
								helper: "Fields dropped (top first) when the name is too long.",
								children: [
									/* @__PURE__ */ p(Ce, {
										values: r.DropOrder,
										onChange: (e) => {
											R("DropOrder", e);
										},
										ordered: !0,
										placeholder: "Add field, press Enter"
									}),
									/* @__PURE__ */ p(Ee, {
										tokens: Rt,
										values: r.DropOrder,
										onAdd: (e) => {
											R("DropOrder", r.DropOrder.includes(e) ? r.DropOrder : [...r.DropOrder, e]);
										}
									}),
									/* @__PURE__ */ p(fn, { values: r.DropOrder })
								]
							}),
							/* @__PURE__ */ p(z, {
								label: "Duplicate suffix format",
								helper: "{n} = a counter added only when a name already exists, e.g. name (1).mp4.",
								children: /* @__PURE__ */ p(be, {
									value: r.DuplicateSuffixFormat,
									onChange: (e) => {
										R("DuplicateSuffixFormat", e);
									},
									options: un,
									customPlaceholder: " ({n})"
								})
							})
						]
					}),
					/* @__PURE__ */ m(Pe, {
						title: "Excludes",
						summary: "Skip items by tag, studio, or source path — evaluated before any routing",
						children: [
							/* @__PURE__ */ p(U, {
								title: "Exclude by tag",
								description: "An item carrying any of these tags is skipped — never renamed, never moved. Evaluated before any routing rule.",
								children: /* @__PURE__ */ p(Ue, {
									label: "Tags",
									values: r.ExcludeTags,
									onChange: (e) => {
										R("ExcludeTags", e);
									},
									placeholder: "Search tags…"
								})
							}),
							/* @__PURE__ */ p(U, {
								title: "Exclude by studio",
								description: "An item under any of these studios — or under a child of one — is skipped entirely. Evaluated before any routing rule.",
								children: /* @__PURE__ */ p(He, {
									label: "Studios",
									values: r.ExcludeStudioIds,
									onChange: (e) => {
										R("ExcludeStudioIds", e);
									},
									placeholder: "Search studios…"
								})
							}),
							/* @__PURE__ */ p(U, {
								title: "Exclude by source path",
								description: "An item whose source path matches a rule is skipped entirely. Evaluated before any routing rule. An exact match or a regex.",
								children: /* @__PURE__ */ p(De, {
									rows: r.ExcludePaths,
									onChange: (e) => {
										R("ExcludePaths", e);
									},
									makeRow: () => ({
										Pattern: "",
										IsRegex: !1
									}),
									renderRow: (e, t, n) => /* @__PURE__ */ m(f, { children: [
										/* @__PURE__ */ p(z, {
											label: "Source path",
											children: /* @__PURE__ */ p(B, {
												value: e.Pattern,
												onChange: (e) => {
													n({ Pattern: e });
												},
												mono: !0,
												placeholder: "Exact path or regex"
											})
										}),
										/* @__PURE__ */ p(H, {
											label: "Match as a regex",
											checked: e.IsRegex,
											onChange: (e) => {
												n({ IsRegex: e });
											}
										}),
										/* @__PURE__ */ p(ke, {
											pattern: e.Pattern,
											isRegex: e.IsRegex
										})
									] }),
									addLabel: "Add exclude rule"
								})
							})
						]
					}),
					/* @__PURE__ */ m(Pe, {
						title: "Field rewriting & name shaping",
						summary: "Literal token replacements, article stripping, and name shaping",
						children: [
							/* @__PURE__ */ p(U, {
								title: "Per-token replacements",
								description: "A literal find/replace on a single token's value, before the name is shaped. The target is a canonical token name (e.g. studio, title), matched case-insensitively.",
								children: /* @__PURE__ */ p(De, {
									rows: r.FieldReplacers,
									onChange: (e) => {
										R("FieldReplacers", e);
									},
									makeRow: () => ({
										TargetToken: on[0].value,
										Find: "",
										Replace: ""
									}),
									renderRow: (e, t, n) => {
										let r = on.some((t) => t.value === e.TargetToken) ? on : [...on, {
											value: e.TargetToken,
											label: `${e.TargetToken} (unknown)`
										}];
										return /* @__PURE__ */ m(f, { children: [
											/* @__PURE__ */ p(z, {
												label: "Target token",
												children: /* @__PURE__ */ p(V, {
													value: e.TargetToken,
													onChange: (e) => {
														n({ TargetToken: e });
													},
													options: r
												})
											}),
											/* @__PURE__ */ p(z, {
												label: "Find",
												helper: "Literal text to match. Empty does nothing.",
												children: /* @__PURE__ */ p(B, {
													value: e.Find,
													onChange: (e) => {
														n({ Find: e });
													},
													placeholder: "Text to find"
												})
											}),
											/* @__PURE__ */ p(z, {
												label: "Replace with",
												children: /* @__PURE__ */ p(B, {
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
							/* @__PURE__ */ m(U, {
								title: "Strip leading article",
								children: [/* @__PURE__ */ p(H, {
									label: "Strip a leading article from the title",
									checked: r.StripLeadingArticles,
									onChange: (e) => {
										R("StripLeadingArticles", e);
									},
									helper: "Removes a single leading article and the whitespace after it from the title, at most once (case-insensitive) — a word merely starting with an article, and a mid-title article, are left alone."
								}), /* @__PURE__ */ p(z, {
									label: "Articles",
									children: /* @__PURE__ */ p(Ce, {
										values: r.Articles,
										onChange: (e) => {
											R("Articles", e);
										},
										placeholder: "Add article, press Enter"
									})
								})]
							}),
							/* @__PURE__ */ p(H, {
								label: "Squeeze studio names",
								checked: r.SqueezeStudioNames,
								onChange: (e) => {
									R("SqueezeStudioNames", e);
								},
								helper: "Removes all spaces from the studio value so one studio renders to one stable folder name."
							}),
							/* @__PURE__ */ p(H, {
								label: "Drop a performer already in the title",
								checked: r.PreventTitlePerformer,
								onChange: (e) => {
									R("PreventTitlePerformer", e);
								},
								helper: "Drops a performer whose name already appears as a whole word in the title."
							}),
							/* @__PURE__ */ p(H, {
								label: "Collapse repeated folder segments",
								checked: r.PreventConsecutiveSegments,
								onChange: (e) => {
									R("PreventConsecutiveSegments", e);
								},
								helper: "Collapses consecutive duplicate folder path segments to one — affects the folder path, not the filename."
							})
						]
					})
				]
			}),
			/* @__PURE__ */ p("div", {
				id: "rename-undo-section",
				children: /* @__PURE__ */ p(ht, { refreshKey: 0 })
			}),
			/* @__PURE__ */ p(mn, {
				dirty: P,
				saving: y,
				saveError: C,
				savedFlash: T,
				canSave: ue,
				onSave: () => void ve(),
				onDiscard: () => {
					s(c);
				}
			})
		]
	});
}
//#endregion
//#region src/RenamePage.tsx
function _n() {
	return /* @__PURE__ */ p(gn, {});
}
//#endregion
//#region src/preview.ts
function vn(e) {
	if (!e) return e;
	let t = Math.max(e.lastIndexOf("/"), e.lastIndexOf("\\"));
	return t >= 0 ? e.slice(t + 1) : e;
}
var yn = 5;
function bn(e) {
	let t = e / (1024 * 1024 * 1024);
	return t >= 10 ? `${Math.round(t)} GB` : `${t.toFixed(1)} GB`;
}
function xn(e) {
	return (e?.volumePairs ?? []).map((e) => `↪ ${e.count} item${e.count === 1 ? "" : "s"} (${bn(e.bytes)}) move from ${e.from} to ${e.to}.`);
}
function Sn(e) {
	return e === "Heavy" ? "This is a LARGE cross-drive move — files will be COPIED across drives, which can take a while. Click OK only if you are sure; Cancel to stop. You can undo this afterwards." : e === "Standard" ? "This moves files across drives. Click OK to proceed, or Cancel to stop. You can undo this afterwards." : "Click OK to rename, or Cancel to stop. You can undo this afterwards.";
}
function Cn(e, t) {
	let n = e.filter((e) => e.status === "Renamer" || e.status === "Move"), r = n.length, i = e.length, a = e.filter((e) => e.status === "SkipGated").length, o = e.filter((e) => e.status === "SkipCollision").length, s = e.filter((e) => e.status === "SkipLocked").length, c = a + o + s, l = n.filter((e) => e.suffixed).length, u = n.filter((e) => e.sanitized).length, d = [];
	if (c > 0) {
		let e = [];
		if (a > 0 && e.push(`${a} need a required field`), o > 0 && e.push(`${o} have a name conflict`), s > 0 && e.push(`${s} are in use`), e.length === 1) {
			let e = a > 0 ? "needs a required field" : o > 0 ? "name conflict" : "in use";
			d.push(`⚠ ${c} skipped (${e}).`);
		} else d.push(`⚠ ${c} skipped — ${e.join(", ")}.`);
	}
	u > 0 && d.push(`⚠ ${u} had illegal characters cleaned up.`), l > 0 && d.push(`⚠ ${l} got a number added to avoid a name clash (e.g. "name (1)").`);
	let f = xn(t), p = d.length > 0 ? `${d.join("\n")}\n\n` : "", m = f.length > 0 ? `${f.join("\n")}\n\n` : "";
	if (r === 0) return {
		text: `Nothing will be renamed — all ${i} selected item${i === 1 ? "" : "s"} are skipped or already named correctly.\n\n` + p + "Click OK to dismiss.",
		willRenameCount: 0
	};
	let h = r === i ? `Rename ${r} selected item${r === 1 ? "" : "s"}?` : `Rename ${r} of ${i} selected items?`, g = n.slice(0, yn).map((e) => `  ${vn(e.oldFullPath)}  →  ${e.newBasename || vn(e.newFullPath)}`), _ = r - g.length;
	_ > 0 && g.push(`  … and ${_} more.`);
	let v = Sn(t?.confirmLevel ?? "Light");
	return {
		text: `${h}\n\n` + p + m + `Examples:\n${g.join("\n")}\n\n` + v,
		willRenameCount: r
	};
}
//#endregion
//#region src/renameSelected.ts
var wn = "com.alextomas955.renamer", Tn = `/extensions/${wn}/preview`, En = `/extensions/${wn}/renamer`;
async function Dn(e, t) {
	let n = JSON.stringify({
		EntityType: t.entityType,
		EntityIds: t.entityIds
	}), r = await _(Tn, {
		method: "POST",
		body: n
	}), { text: i, willRenameCount: a } = Cn(r.items, r.summary);
	if (!window.confirm(i) || a === 0) return { cancelled: !0 };
	try {
		await _(En, {
			method: "POST",
			body: n
		});
	} catch (e) {
		if (e instanceof v) throw e;
	}
	return {};
}
//#endregion
//#region src/index.ts
var On = h({ components: { RenamerPage: _n } });
On.actionHandlers = { renamerSelected: Dn };
//#endregion
export { On as default };
