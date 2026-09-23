import type { ReactNode } from "react";
import Link from "@docusaurus/Link";
import Heading from "@theme/Heading";
import Layout from "@theme/Layout";
import renamerShot from "../../../extensions/Renamer/docs/img/live-preview.jpg";
import styles from "./index.module.css";

type Extension = {
  name: string;
  summary: string;
  facts: string[];
  image: string;
  imageAlt: string;
  docs: string;
  quickStart: string;
};

// One entry per published extension, in the order the home page lists them.
const extensions: Extension[] = [
  {
    name: "Renamer",
    summary:
      "Gives every file a consistent name built from its metadata, and sorts files into folders such as one per studio and year.",
    facts: ["Videos, images, audio and text documents", "Dry run first, undo for 7 days"],
    image: renamerShot,
    imageAlt:
      "Renamer's live preview: a file's old name crossed out above the new name built from its metadata.",
    docs: "/extensions/renamer",
    quickStart: "/extensions/renamer/quick-start",
  },
];

const steps = [
  {
    title: "Install from Cove",
    body: "Open Settings, then Discover under Extensions. Search for an extension and select Install.",
  },
  {
    title: "Follow its quick start",
    body: "Each extension has a short guide with a screenshot for every step.",
  },
  {
    title: "Look things up when you need to",
    body: "How-to guides, troubleshooting and a full settings reference for each extension.",
  },
];

export default function Home(): ReactNode {
  return (
    <Layout
      title="Cove Extensions"
      description="Community extensions that add features to the Cove media library."
    >
      <header className={styles.hero}>
        <div className="container">
          <p className={styles.eyebrow}>Community extensions for Cove</p>
          <h1 className={styles.title}>Add more to your Cove library</h1>
          <p className={styles.lead}>
            Extensions for the self-hosted <a href="https://yourcove.net">Cove</a> media library.
            Each one installs from Cove&apos;s own Discover page and comes with step-by-step guides.
          </p>
          <div className={styles.actions}>
            <Link className="button button--primary button--lg" href="#extensions">
              Browse extensions
            </Link>
            <Link className="button button--secondary button--lg" to="/contributing">
              Build an extension
            </Link>
          </div>
        </div>
      </header>

      <main className="container">
        <section className={styles.section} aria-labelledby="extensions">
          <Heading as="h2" id="extensions">
            Extensions
          </Heading>
          <div className={styles.grid}>
            {extensions.map((extension) => (
              <article key={extension.name} className={styles.extension}>
                <Link to={extension.docs} className={styles.shot}>
                  <img src={extension.image} alt={extension.imageAlt} loading="lazy" />
                </Link>
                <div className={styles.extensionBody}>
                  <h3>{extension.name}</h3>
                  <p>{extension.summary}</p>
                  <ul className={styles.facts}>
                    {extension.facts.map((fact) => (
                      <li key={fact}>{fact}</li>
                    ))}
                  </ul>
                  <div className={styles.links}>
                    <Link className="button button--primary" to={extension.quickStart}>
                      Quick start
                    </Link>
                    <Link className="button button--secondary" to={extension.docs}>
                      Documentation
                    </Link>
                  </div>
                </div>
              </article>
            ))}
            <article className={styles.upcoming}>
              <h3>More on the way</h3>
              <p>
                New extensions are added here as they are released. Watch the{" "}
                <a href="https://github.com/alextomas955/cove-extensions">repository</a> to hear
                about them.
              </p>
            </article>
          </div>
        </section>

        <section className={styles.section} aria-labelledby="how-heading">
          <h2 id="how-heading">How it works</h2>
          <ol className={styles.steps}>
            {steps.map((step) => (
              <li key={step.title}>
                <h3>{step.title}</h3>
                <p>{step.body}</p>
              </li>
            ))}
          </ol>
        </section>

        <p className={styles.notice}>
          These extensions are maintained by{" "}
          <a href="https://github.com/alextomas955">alextomas955</a>. They are not affiliated with,
          or endorsed by, the Cove project.
        </p>
      </main>
    </Layout>
  );
}
