/**
 * A generated wire shape with the optional markers dropped.
 *
 * The generator marks every member of a C# record with a default initializer optional, because a
 * request may omit it. A response never does - the server writes all of them - and a panel control
 * needs a value, so the response side is modeled as complete. A backend test holds the server to it.
 */
export type Complete<T> = T extends readonly (infer E)[]
  ? Complete<E>[]
  : T extends object
    ? { [K in keyof T]-?: Complete<T[K]> }
    : T;
