#!/usr/bin/env python3
"""Writes one studio, performer or scene straight into a Whisparr instance's own database.

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

# A scene is two rows: its catalogue entry, and the entry the instance monitors. The identifier the
# per-scene route narrows on is StashId, which the caller supplies as the foreign id, so both columns
# carry it and the route the extension reads by is the one this seeds for.
SCENE_METADATA_TABLE = "MovieMetadata"
SCENE_TABLE = "Movies"

# Declared NOT NULL on MovieMetadata and carrying no default.
SCENE_METADATA_REQUIRED = {
    "MetadataSource": 0,
    "Images": "[]",
    "OriginalLanguage": 1,
    "Status": 0,
    "Runtime": 0,
    "Recommendations": "[]",
    "ItemType": 0,
}

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


# Where each generation keeps its catalogue, as the image lays it out. Chosen here rather than
# passed in, so the caller holds no second copy of a path that belongs to this image.
V3_DATABASE = "/config/whisparr3.db"
V2_DATABASE = "/config/whisparr2.db"


def database_for(generation: str) -> str:
    return V2_DATABASE if generation == "v2" else V3_DATABASE


def write_scene(connection, args) -> int:
    """Writes one scene's catalogue entry and the entry the instance monitors."""
    metadata = {
        "ForeignId": args.foreign_id,
        "StashId": args.foreign_id,
        "Title": args.title,
        "CleanTitle": "".join(args.title.lower().split()),
        "SortTitle": args.title.lower(),
        **SCENE_METADATA_REQUIRED,
    }

    # Written only where the caller named one, so a seed that says nothing about a studio leaves the
    # column as the instance's own default rather than an empty link.
    if args.studio_foreign_id:
        metadata["StudioForeignId"] = args.studio_foreign_id
        metadata["StudioTitle"] = args.studio_title or args.studio_foreign_id
    if args.performer_foreign_ids:
        metadata["PerformerForeignIds"] = json.dumps(args.performer_foreign_ids.split(","))
        metadata["PerformerNames"] = json.dumps(
            (args.performer_names or args.performer_foreign_ids).split(",")
        )
    if args.release_date:
        metadata["ReleaseDateUtc"] = args.release_date
    names = ", ".join(f'"{column}"' for column in metadata)
    placeholders = ", ".join("?" for _ in metadata)
    metadata_id = connection.execute(
        f'INSERT INTO "{SCENE_METADATA_TABLE}" ({names}) VALUES ({placeholders})',
        tuple(metadata.values()),
    ).lastrowid

    # The path is the scene's own and is what the instance addresses the entry on disk by, so it
    # carries the foreign id rather than the title: two scenes seeded under one title would otherwise
    # be two entries under one path.
    return connection.execute(
        f'INSERT INTO "{SCENE_TABLE}" ("Path", "Monitored", "QualityProfileId", "MovieMetadataId",'
        ' "Tags", "Added") VALUES (?, ?, ?, ?, ?, '
        "datetime('now'))",
        (
            f"{args.root_folder_path.rstrip('/')}/{args.foreign_id}",
            1 if args.monitored == "true" else 0,
            args.quality_profile_id,
            metadata_id,
            "[]",
        ),
    ).lastrowid


def main() -> None:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--generation", required=True, choices=("v3", "v2"))
    parser.add_argument("--kind", required=True, choices=sorted([*TABLES, "scene"]))
    parser.add_argument("--foreign-id", required=True, help="the id the instance is addressed by")
    parser.add_argument("--title", required=True)
    parser.add_argument("--quality-profile-id", required=True, type=int)
    parser.add_argument("--root-folder-path", required=True)
    parser.add_argument("--monitored", default="false", choices=["true", "false"])
    # A scene the instance lists under a studio or a performer. The catalogue surface reads an
    # entity's own scenes, and an instance lists a scene under an entity only through these columns.
    parser.add_argument("--studio-foreign-id", default=None)
    parser.add_argument("--studio-title", default=None)
    parser.add_argument("--performer-foreign-ids", default=None, help="comma separated")
    parser.add_argument("--performer-names", default=None, help="comma separated")
    parser.add_argument("--release-date", default=None, help="yyyy-mm-dd")
    args = parser.parse_args()

    if args.kind == "scene":
        connection = sqlite3.connect(database_for(args.generation))
        try:
            row = write_scene(connection, args)
            connection.commit()
            print(json.dumps({"kind": args.kind, "foreignId": args.foreign_id, "id": row}))
        finally:
            connection.close()
        return

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
    connection = sqlite3.connect(database_for(args.generation))
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
