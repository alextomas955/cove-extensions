// The bundler resolves an imported image to its published URL.
declare module "*.jpg" {
  const src: string;
  export default src;
}
