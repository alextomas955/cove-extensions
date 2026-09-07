/**
 * SceneStateGlyph — the one lucide glyph for a Whisparr management state, rendered from the pure
 * {@link ../lib/sceneStateVisual} descriptors — the enum-keyed record and the standalone indeterminate one —
 * each a plain (iconKey + host color token + filled) triple. The single
 * place an iconKey resolves to a concrete lucide glyph, so the scene-status card badge and the discovery Missing
 * card render an IDENTICAL status glyph. Lives in common/ because two feature slices reuse it.
 */
// `CircleHelp` names the question-mark-in-a-circle glyph on BOTH sides of the externalized boundary, which
// `CircleQuestionMark` does not: lucide-react is a host import-map external, so the name must exist in the
// runtime module the host serves as well as in the version resolved for typechecking. The host's shim predates
// the 1.x rename and exports only `CircleHelp`; the pinned 1.25.0 keeps it as an alias of the renamed icon. An
// ESM named import that the host module lacks is a load-time SyntaxError that fails the WHOLE bundle, so every
// surface in this extension goes dark — a build and typecheck cannot see it.
import { Ban, Bookmark, Circle, CircleDashed, CircleHelp } from "lucide-react";

const GLYPH: Record<string, typeof Bookmark> = {
  bookmark: Bookmark,
  circle: Circle,
  circleDashed: CircleDashed,
  ban: Ban,
  circleQuestion: CircleHelp,
};

export function SceneStateGlyph({
  iconKey,
  color,
  filled,
  className = "h-3.5 w-3.5",
}: {
  iconKey: string;
  color: string;
  filled: boolean;
  className?: string;
}) {
  const Icon = GLYPH[iconKey] ?? Circle;
  return (
    <Icon className={`${className} ${color}`} fill={filled ? "currentColor" : "none"} aria-hidden />
  );
}
