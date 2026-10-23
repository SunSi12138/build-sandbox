-- Original schema from 4f63938:server/Database.cs
CREATE TABLE settings (
    id INTEGER PRIMARY KEY CHECK(id=1), street_name TEXT NOT NULL, issuing_unit TEXT NOT NULL,
    default_inspector TEXT NOT NULL, notice_template TEXT NOT NULL, jpeg_quality INTEGER NOT NULL CHECK(jpeg_quality BETWEEN 1 AND 100)
);
CREATE TABLE communities (id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE, title TEXT NOT NULL);
CREATE TABLE point_types (id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE);
CREATE TABLE points (
    id TEXT PRIMARY KEY, name TEXT NOT NULL, community_id TEXT NOT NULL REFERENCES communities(id) ON DELETE RESTRICT,
    type_id TEXT NOT NULL REFERENCES point_types(id) ON DELETE RESTRICT, address TEXT NOT NULL,
    contact TEXT NOT NULL DEFAULT '', phone TEXT NOT NULL DEFAULT '',
    frequency TEXT NOT NULL CHECK(frequency IN ('weekly','monthly','quarterly','irregular')),
    notes TEXT NOT NULL DEFAULT '', disabled INTEGER NOT NULL DEFAULT 0 CHECK(disabled IN (0,1)),
    longitude REAL CHECK(longitude IS NULL OR longitude BETWEEN -180 AND 180),
    latitude REAL CHECK(latitude IS NULL OR latitude BETWEEN -90 AND 90),
    temporary INTEGER NOT NULL DEFAULT 0 CHECK(temporary IN (0,1))
);
CREATE TABLE inspections (
    id TEXT PRIMARY KEY, point_id TEXT NOT NULL REFERENCES points(id) ON DELETE RESTRICT,
    inspection_date TEXT NOT NULL, inspector TEXT NOT NULL, result TEXT NOT NULL CHECK(result IN ('normal','issues')),
    notes TEXT NOT NULL DEFAULT '', notice_number TEXT NOT NULL DEFAULT '',
    point_name TEXT NOT NULL, community_id TEXT NOT NULL REFERENCES communities(id) ON DELETE RESTRICT,
    community_name TEXT NOT NULL, community_title TEXT NOT NULL,
    type_id TEXT NOT NULL REFERENCES point_types(id) ON DELETE RESTRICT, type_name TEXT NOT NULL,
    address TEXT NOT NULL, contact TEXT NOT NULL, phone TEXT NOT NULL
);
CREATE TABLE issues (
    id TEXT PRIMARY KEY, inspection_id TEXT NOT NULL REFERENCES inspections(id) ON DELETE RESTRICT,
    description TEXT NOT NULL, requirement TEXT NOT NULL DEFAULT '', deadline TEXT NOT NULL, responsible_unit TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'pending' CHECK(status IN ('pending','feedback','closed')),
    feedback_date TEXT, feedback_notes TEXT NOT NULL DEFAULT '', review_date TEXT,
    review_conclusion TEXT NOT NULL DEFAULT '', closed_date TEXT
);
CREATE TABLE issue_actions (
    id TEXT PRIMARY KEY, issue_id TEXT NOT NULL REFERENCES issues(id) ON DELETE RESTRICT,
    kind TEXT NOT NULL CHECK(kind IN ('feedback','review','return')), action_date TEXT NOT NULL, notes TEXT NOT NULL
);
CREATE TABLE campaign_types (id TEXT PRIMARY KEY, name TEXT NOT NULL UNIQUE);
    CREATE TABLE campaigns (
        id TEXT PRIMARY KEY, name TEXT NOT NULL,
        type_id TEXT NOT NULL REFERENCES campaign_types(id) ON DELETE RESTRICT, type_name TEXT NOT NULL,
        community_id TEXT NOT NULL REFERENCES communities(id) ON DELETE RESTRICT, community_name TEXT NOT NULL,
        location TEXT NOT NULL, campaign_date TEXT NOT NULL, end_date TEXT,
        organizer TEXT NOT NULL DEFAULT '', participants TEXT NOT NULL DEFAULT '',
        participant_count INTEGER CHECK(participant_count IS NULL OR participant_count >= 0),
        vehicle_count INTEGER CHECK(vehicle_count IS NULL OR vehicle_count >= 0),
        garbage_tons REAL CHECK(garbage_tons IS NULL OR garbage_tons >= 0),
        area_sqm REAL CHECK(area_sqm IS NULL OR area_sqm >= 0),
        content TEXT NOT NULL, outcome TEXT NOT NULL DEFAULT '', notes TEXT NOT NULL DEFAULT '',
        CHECK(end_date IS NULL OR end_date >= campaign_date)
    );
CREATE TABLE photos (
        id TEXT PRIMARY KEY, inspection_id TEXT REFERENCES inspections(id) ON DELETE RESTRICT,
        issue_id TEXT REFERENCES issues(id) ON DELETE RESTRICT,
        action_id TEXT REFERENCES issue_actions(id) ON DELETE RESTRICT,
        point_id TEXT REFERENCES points(id) ON DELETE RESTRICT,
        campaign_id TEXT REFERENCES campaigns(id) ON DELETE RESTRICT,
        phase TEXT NOT NULL CHECK(phase IN ('site','after','review','point','before','process')),
        relative_path TEXT NOT NULL UNIQUE, width INTEGER NOT NULL CHECK(width>0), height INTEGER NOT NULL CHECK(height>0),
        uploaded_at TEXT NOT NULL, has_thumb INTEGER NOT NULL DEFAULT 0 CHECK(has_thumb IN (0,1)),
        CHECK((inspection_id IS NOT NULL)+(issue_id IS NOT NULL)+(point_id IS NOT NULL)+(campaign_id IS NOT NULL) <= 1),
        CHECK(action_id IS NULL OR issue_id IS NOT NULL),
        CHECK(inspection_id IS NULL OR phase='site'),
        CHECK(issue_id IS NULL OR phase IN ('site','after','review')),
        CHECK(point_id IS NULL OR phase='point'),
        CHECK(campaign_id IS NULL OR phase IN ('before','process','after'))
    );
CREATE INDEX ix_points_community_type ON points(community_id,type_id,disabled);
CREATE INDEX ix_inspections_point_date ON inspections(point_id,inspection_date);
CREATE INDEX ix_inspections_community_date ON inspections(community_id,inspection_date);
CREATE INDEX ix_issues_status_deadline ON issues(status,deadline);
CREATE INDEX ix_issues_inspection ON issues(inspection_id);
CREATE INDEX ix_actions_issue ON issue_actions(issue_id);
CREATE INDEX ix_photos_inspection ON photos(inspection_id);
CREATE INDEX ix_photos_issue ON photos(issue_id);
CREATE INDEX ix_photos_unattached ON photos(uploaded_at) WHERE inspection_id IS NULL AND issue_id IS NULL AND point_id IS NULL AND campaign_id IS NULL;
CREATE INDEX ix_photos_point ON photos(point_id);
CREATE INDEX ix_photos_campaign ON photos(campaign_id);
CREATE INDEX ix_campaigns_date ON campaigns(campaign_date,community_id,type_id);
CREATE INDEX ix_points_temporary ON points(temporary,disabled);
PRAGMA application_id=1397048143;
PRAGMA user_version=2;
