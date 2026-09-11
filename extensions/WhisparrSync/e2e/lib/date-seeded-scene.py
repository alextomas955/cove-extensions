#!/usr/bin/env python3
"""Gives a seeded scene the facts its instance needs to search for it and to announce it.

This generation searches for a scene by its SITE and its DATE, not by its title. A catalogue row
carrying neither produces no query at all: the interactive search answers an empty list, the indexer
is never asked, and nothing names the reason.

The shared entity seeder writes only the columns the schema declares NOT NULL, which is the right
rule for it and leaves both of these unset. This fills them in afterwards for the one spec that has
to make the instance go and search.

Only columns the table actually declares are written, so a build that renames one is a column
skipped here rather than an insert that fails. Prints the columns it set.
"""

import argparse
import json
import sqlite3


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    # The caller names the database; this file chooses none.
    parser.add_argument("--db", required=True)
    parser.add_argument("--foreign-id", required=True)
    parser.add_argument("--release-date", required=True, help="ISO-8601, the date the search uses")
    parser.add_argument("--studio-title", required=True, help="the site the search names")
    args = parser.parse_args()

    connection = sqlite3.connect(args.db)
    try:
        declared = {
            row[1] for row in connection.execute("PRAGMA table_info(MovieMetadata)").fetchall()
        }
        # Runtime is deliberately left at zero. It is stored in MINUTES, and a runtime the fixture
        # cannot match makes sample detection hold the import: a 150-second file against a 150-minute
        # expectation is a sample by any measure. At zero the check falls back to a 15-second floor,
        # which the fixture clears honestly.
        #
        # Both date columns, not only the one the search reads. The instance builds its outbound
        # notification from a type that reads ReleaseDateUtc without checking, and a null one throws
        # "Nullable object must have a value" while building the payload. The import still completes,
        # so what is lost is only the notification, and nothing says so anywhere a caller can see.
        #
        # This build carries no InCinemas, PhysicalRelease or DigitalRelease: it replaced them with
        # this pair. A name from the other lineage is skipped here rather than written, which is why
        # the declared set is printed.
        wanted = {
            "ReleaseDate": args.release_date,
            "ReleaseDateUtc": f"{args.release_date} 00:00:00",
            "Year": int(args.release_date[:4]),
            "StudioTitle": args.studio_title,
            "StudioForeignId": args.studio_title.lower().replace(" ", ""),
        }
        setting = {name: value for name, value in wanted.items() if name in declared}
        if not setting:
            raise SystemExit("MovieMetadata declares none of the columns a scene search reads.")

        assignments = ", ".join(f'"{name}" = ?' for name in setting)
        changed = connection.execute(
            f'UPDATE MovieMetadata SET {assignments} WHERE "ForeignId" = ?',
            [*setting.values(), args.foreign_id],
        ).rowcount
        connection.commit()
    finally:
        connection.close()

    if changed != 1:
        raise SystemExit(
            f"{changed} MovieMetadata rows carry ForeignId {args.foreign_id}; expected exactly one."
        )
    print(json.dumps({"set": sorted(setting), "declared": sorted(declared)}))


if __name__ == "__main__":
    main()
