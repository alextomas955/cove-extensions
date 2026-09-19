/**
 * The select options for a wire enum, built from a label map that must cover it.
 *
 * A control offering a closed set has to list the members somewhere, and a list written out by hand
 * says nothing about the enum it came from: a member added to the C# enum would simply be missing from
 * the control, with nothing failing. Taking the options from a complete `Record` makes that a build
 * error instead. Insertion order is the order the control shows.
 */
export function optionsFor<T extends string>(
  labels: Record<T, string>,
): readonly { value: T; label: string }[] {
  return (Object.keys(labels) as T[]).map((value) => ({ value, label: labels[value] }));
}
