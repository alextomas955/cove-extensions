#!/usr/bin/env python3
"""Writes one v2 scene straight into a Whisparr instance's own database.

A scene on this generation is two rows, not one: the site is a Series and the scene is an Episode
under it. That is the whole reason this file exists beside the shared entity seeder, which writes the
other generation's single catalogue row and is wired for it alone.

The datastore rather than the add route, for the same reason the shared seeder gives: an add resolves
its foreign id against the vendor's metadata service, which is a third party this harness does not
control and cannot pin.

Run INSIDE the container, as the app's own user, and never against a database this harness did not
create. The caller names the database; this file chooses none.

Only the columns the schema declares NOT NULL are written, plus the ones the caller decides. Prints
one JSON object naming the rows it wrote, so a caller reads the instance-side ids it will address
them by rather than assuming the sequence.
"""

import argparse
import json
import sqlite3

# Declared NOT NULL on Series and carrying no default, read off the pinned v2 build.
SERIES_REQUIRED = {
    "Status": 0,
    "Images": "[]",
    "Runtime": 0,
    "UseSceneNumbering": 0,
    "OriginalLanguage": 1,
    "MonitorNewItems": 0,
    "SeriesType": 0,
    "Seasons": '[{"seasonNumber": 1, "monitored": true}]',
    "Tags": "[]",
}

# Declared NOT NULL on Episodes and carrying no default.
EPISODE_REQUIRED = {
    "SeasonNumber": 1,
    "Runtime": 0,
}


# This seeder writes v2's catalogue and no other, so the path is stated here rather than passed in.
V2_DATABASE = "/config/whisparr2.db"


def insert(connection, table, values):
    names = ", ".join(f'"{column}"' for column in values)
    placeholders = ", ".join("?" for _ in values)
    cursor = connection.execute(
        f"INSERT INTO {table} ({names}) VALUES ({placeholders})", list(values.values())
    )
    return cursor.lastrowid


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    # The site's own identifier in the instance's namespace. Declared NOT NULL and UNIQUE, so two
    # seeds under one id fail here rather than leaving the second silently unwritten.
    parser.add_argument("--site-id", required=True, type=int)
    parser.add_argument("--site-title", required=True)
    parser.add_argument("--root-folder-path", required=True)
    parser.add_argument("--quality-profile-id", required=True, type=int)
    # What the per-scene surface addresses the episode by on this generation.
    parser.add_argument("--scene-external-id", required=True)
    parser.add_argument("--scene-title", required=True)
    # The date the search names. This generation searches a scene by its site and its date, so an
    # episode carrying neither produces no query at all.
    parser.add_argument("--air-date", required=True)
    # What the site starts as. A caller driving both monitoring directions seeds it off, so the first
    # gesture is the one that turns it on and neither direction is read off a flag it started at.
    parser.add_argument("--monitored", default="true")
    args = parser.parse_args()

    connection = sqlite3.connect(V2_DATABASE)
    try:
        series_id = insert(
            connection,
            "Series",
            {
                "TvdbId": args.site_id,
                "Title": args.site_title,
                "CleanTitle": "".join(args.site_title.lower().split()),
                "SortTitle": args.site_title.lower(),
                "TitleSlug": str(args.site_id),
                "Path": f"{args.root_folder_path.rstrip('/')}/{args.site_title}",
                "QualityProfileId": args.quality_profile_id,
                "Year": int(args.air_date[:4]),
                # Both, because the payload this instance builds for its outbound notification reads
                # the UTC one without checking it for null.
                "FirstAired": args.air_date,
                "LastAired": args.air_date,
                "Monitored": 1 if args.monitored == "true" else 0,
                **SERIES_REQUIRED,
            },
        )

        episode_id = insert(
            connection,
            "Episodes",
            {
                "SeriesId": series_id,
                "Title": args.scene_title,
                "ExternalId": args.scene_external_id,
                "Monitored": 1,
                "AirDate": args.air_date,
                "AirDateUtc": f"{args.air_date} 00:00:00",
                "AbsoluteEpisodeNumber": 1,
                **EPISODE_REQUIRED,
            },
        )
        connection.commit()
    finally:
        connection.close()

    print(json.dumps({"seriesId": series_id, "episodeId": episode_id}))


if __name__ == "__main__":
    main()
