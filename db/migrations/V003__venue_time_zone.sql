-- A show time belongs to the venue, not the viewer. Storing the venue's IANA
-- zone lets every client render "8:00 PM at The Chapel" identically, regardless
-- of where the browser is. The instant itself is already correct in
-- show_event.starts_at (TIMESTAMP WITH TIME ZONE); this is purely for display.
--
-- IANA rather than a fixed offset, because an offset is wrong half the year:
-- The Chapel is -08:00 in November and -07:00 in June.

ALTER TABLE venue ADD time_zone VARCHAR2(64) DEFAULT 'UTC' NOT NULL;

UPDATE venue SET time_zone = 'America/Los_Angeles' WHERE name = 'The Chapel';

COMMIT;
