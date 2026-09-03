#!/usr/bin/env python3
"""Writes one studio or performer straight into a Whisparr instance's own database.

The datastore rather than the add route, and that is the whole reason this file exists: an add
resolves its foreign id against the vendor's metadata service, so it is a call to a third party this
harness does not control and cannot pin. An entity's mere existence would then depend on someone
else's uptime.

Run INSIDE the container, as the app's own user, and never against a database this harness did not
create. The caller names the database; this file chooses none.

Only the columns the schema declares NOT NULL are written, plus the two the caller decides
(monitored, and the title the page is found by). Everything else keeps its declared default, so a
column added by a later build arrives with the value that build chose rather than one written here.

Prints one JSON object naming the row it wrote, so a caller reads the instance-side id it will be
addressed by rather than assuming the sequence.
"""

import argparse
import json
import sqlite3

# The two tables this harness seeds, and the columns each declares NOT NULL with no default. Read
# off `sqlite_master` on the pinned v3 build rather than transcribed from a schema document: a
# column this map omits is one the insert fails on, naming the column.
#
# `ForeignId` is the identifier the instance is addressed by and is UNIQUE, so two seeds under one
# id fail here rather than leaving the second silently unwritten.
TABLES = {
    "studio": {
        "table": "Studios",
        "name_column": "Title",
        "clean_column": "CleanTitle",
        # Declared NOT NULL on Studios and carrying no default.
        "required": {
            "Images": "[]",
            "Tags": "[]",
            "MoviesMonitored": 0,
            "Status": 0,
            "TmdbId": 0,
            "MovieCount": 0,
            "SceneCount": 0,
            "TotalMovieCount": 0,
            "TotalSceneCount": 0,
            "SizeOnDisk": 0,
        },
    },
    "performer": {
        "table": "Performers",
        "name_column": "Name",
        "clean_column": "CleanName",
        "required": {
            "Gender": 0,
            "Status": 0,
            "Tags": "[]",
        },
    },
}


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--db", required=True, help="the database to write, named by the caller")
    parser.add_argument("--kind", required=True, choices=sorted(TABLES))
    parser.add_argument("--foreign-id", required=True, help="the id the instance is addressed by")
    parser.add_argument("--title", required=True)
    parser.add_argument("--quality-profile-id", required=True, type=int)
    parser.add_argument("--root-folder-path", required=True)
    parser.add_argument("--monitored", default="false", choices=["true", "false"])
    args = parser.parse_args()

    shape = TABLES[args.kind]
    columns = {
        "ForeignId": args.foreign_id,
        "QualityProfileId": args.quality_profile_id,
        "RootFolderPath": args.root_folder_path,
        # The instance's own add-time search, and the one column here that could cause an
        # acquisition. Always off: this harness seeds catalogue rows and never asks for a download.
        "SearchOnAdd": 0,
        shape["name_column"]: args.title,
        # Lower-cased and stripped of separators, which is the shape the app's own clean column
        # holds. Nothing reads it back, so it only has to be present and consistent.
        shape["clean_column"]: "".join(args.title.lower().split()),
        "Monitored": 1 if args.monitored == "true" else 0,
        **shape["required"],
    }

    placeholders = ", ".join("?" for _ in columns)
    names = ", ".join(f'"{column}"' for column in columns)
    connection = sqlite3.connect(args.db)
    try:
        cursor = connection.execute(
            f'INSERT INTO "{shape["table"]}" ({names}, "Added") '
            f"VALUES ({placeholders}, datetime('now'))",
            tuple(columns.values()),
        )
        connection.commit()
        print(json.dumps({"kind": args.kind, "foreignId": args.foreign_id, "id": cursor.lastrowid}))
    finally:
        connection.close()


if __name__ == "__main__":
    main()
