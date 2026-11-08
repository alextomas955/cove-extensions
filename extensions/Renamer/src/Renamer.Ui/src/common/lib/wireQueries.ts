/**
 * The generated TanStack Query helpers, under a path a call site can read.
 *
 * The generator names that file for the library it targets, so it sits at `wire/@tanstack/
 * react-query.gen`. Re-exporting here keeps that spelling out of every hook, and keeps a change of
 * generator to this one module.
 *
 * Each `*Options` carries the query key and the request; each `*Mutation` carries the request alone.
 * Both come from the committed document, so no hook writes a path, a method or a query key itself.
 */
export * from "../../wire/@tanstack/react-query.gen";
