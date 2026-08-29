"""Writes import history straight into a Whisparr instance's own SQLite database.

Neither generation exposes an API that CREATES history — every route over it reads — so a fixture
that needs an instance with a past has no option but the datastore. Run inside the container with
the app up: both images ship python3 with the stdlib sqlite3 module and no sqlite3 CLI.

Nothing here believes its own writes. The caller reads the rows back through the app's own history
API, because the reader inner-joins history to its parent library row and answers an orphaned row
with an empty page rather than an error.
"""

import argparse
import json
import sqlite3
from datetime import datetime, timedelta, timezone

# The integers written into History.EventType. What each one RENDERS as is read back through the
# app's API and never taken from the OpenAPI enum's order: the stored integers are sparse, so the
# name at a given array index belongs to a different integer.
EVENT_TYPES = (1, 3)

# The stored shape, which is not the shape the API answers with. `quality` is an integer
# quality-definition id; an object in that position fails the column's converter and the request
# 500s. The name stored beside the id is ignored by the reader, so none is written.
QUALITY = json.dumps({"quality": 7, "revision": {"version": 1, "real": 0, "isRepack": False}})

LANGUAGES = json.dumps([1])
EMPTY_JSON_OBJECT = "{}"
EMPTY_JSON_ARRAY = "[]"


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--generation", required=True, choices=("v3", "v2"))
    parser.add_argument("--count", required=True, type=int)
    # Taken as an argument rather than derived from the generation: the one database this process may
    # open is named by its caller, so no default can ever point it at something it does not own.
    parser.add_argument("--db", required=True)
    args = parser.parse_args()

    if args.count < len(EVENT_TYPES):
        parser.error(
            f"--count must be at least {len(EVENT_TYPES)} so the written rows span every event type"
        )

    connection = sqlite3.connect(args.db, timeout=30)
    try:
        # The app is running and holds the database in WAL mode, so a writer can meet a busy lock
        # that resolves on its own.
        connection.execute("PRAGMA busy_timeout = 30000")
        seed = SEEDERS[args.generation]
        rows = seed(connection, args.count)
        connection.commit()
    finally:
        connection.close()

    print(json.dumps({"generation": args.generation, "database": args.db, "rows": rows}))


def seed_v3(connection, count):
    metadata_id = insert(
        connection,
        "MovieMetadata",
        {
            "ForeignId": "cove-e2e-seeded-scene",
            "MetadataSource": 0,
            "StashId": "cove-e2e-seeded-scene",
            "Title": "Cove E2E Seeded Scene",
            "SortTitle": "cove e2e seeded scene",
            "CleanTitle": "coveeeseededscene",
            "Images": EMPTY_JSON_ARRAY,
            "Recommendations": EMPTY_JSON_ARRAY,
            "OriginalLanguage": 1,
            "Status": 0,
            "Runtime": 0,
            "ItemType": 1,
        },
    )
    movie_id = insert(
        connection,
        "Movies",
        {
            "Path": "/media/cove-e2e-seeded-scene",
            "Monitored": 1,
            "QualityProfileId": 1,
            "MovieMetadataId": metadata_id,
            "MovieFileId": 0,
            "Added": timestamp(0),
        },
    )
    return insert_history(connection, count, lambda index: {"MovieId": movie_id})


def seed_v2(connection, count):
    series_id = insert(
        connection,
        "Series",
        {
            "TvdbId": 0,
            "Title": "Cove E2E Seeded Site",
            "TitleSlug": "cove-e2e-seeded-site",
            "CleanTitle": "coveeeseededsite",
            "Status": 0,
            "Images": EMPTY_JSON_ARRAY,
            "Path": "/media/cove-e2e-seeded-site",
            "Monitored": 1,
            "QualityProfileId": 1,
            "Runtime": 0,
            "UseSceneNumbering": 0,
            "OriginalLanguage": 1,
            "Added": timestamp(0),
        },
    )
    episode_id = insert(
        connection,
        "Episodes",
        {
            "SeriesId": series_id,
            "SeasonNumber": 1,
            "Runtime": 0,
            "Monitored": 1,
            "Title": "Cove E2E Seeded Episode",
            "EpisodeFileId": 0,
        },
    )
    return insert_history(
        connection, count, lambda index: {"EpisodeId": episode_id, "SeriesId": series_id}
    )


def insert_history(connection, count, parent_columns):
    rows = []
    for index in range(count):
        event_type = EVENT_TYPES[index % len(EVENT_TYPES)]
        source_title = f"Cove.E2E.Seeded.{index}.1080p.WEB-DL"
        insert(
            connection,
            "History",
            {
                **parent_columns(index),
                "SourceTitle": source_title,
                # Distinct per row and descending, so a reader paging newest-first sees a stable
                # order rather than one the database is free to choose.
                "Date": timestamp(index),
                "Quality": QUALITY,
                "Data": EMPTY_JSON_OBJECT,
                "EventType": event_type,
                "DownloadId": f"COVEE2ESEEDED{index}",
                "Languages": LANGUAGES,
            },
        )
        rows.append({"sourceTitle": source_title, "eventType": event_type})
    return rows


def insert(connection, table, values):
    """Inserts one row, refusing a column set the live schema does not agree with."""
    reject_column_mismatch(connection, table, values)
    columns = ", ".join(values)
    placeholders = ", ".join(["?"] * len(values))
    cursor = connection.execute(
        f"INSERT INTO {table} ({columns}) VALUES ({placeholders})", list(values.values())
    )
    return cursor.lastrowid


def reject_column_mismatch(connection, table, values):
    """Names the disagreement between this file and the schema the pinned build actually shipped.

    A column added, renamed or made NOT NULL by a version bump otherwise surfaces as a bare sqlite3
    message with no hint that a fixture wrote it.
    """
    schema = list(connection.execute(f"PRAGMA table_info({table})"))
    if not schema:
        raise SystemExit(f"whisparr-seed-history: this database has no table named {table}.")

    declared = {column[1] for column in schema}
    unknown = sorted(set(values) - declared)
    if unknown:
        raise SystemExit(
            f"whisparr-seed-history: {table} has no column {', '.join(unknown)}; "
            f"it declares {', '.join(sorted(declared))}."
        )

    # notnull set, no default and not the rowid alias: a column this file must supply a value for.
    missing = sorted(
        column[1]
        for column in schema
        if column[3] and column[4] is None and not column[5] and column[1] not in values
    )
    if missing:
        raise SystemExit(
            f"whisparr-seed-history: {table} requires a value for {', '.join(missing)}, "
            "which this seeder does not write."
        )


def timestamp(minutes_ago):
    return (datetime.now(timezone.utc) - timedelta(minutes=minutes_ago)).strftime(
        "%Y-%m-%d %H:%M:%S"
    )


SEEDERS = {"v3": seed_v3, "v2": seed_v2}


if __name__ == "__main__":
    main()
