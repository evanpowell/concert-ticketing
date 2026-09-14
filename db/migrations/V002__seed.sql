-- Reference data. Pure SQL, no PL/SQL: seats are generated with CONNECT BY.

INSERT INTO venue (name, city) VALUES ('The Chapel', 'San Francisco');

INSERT INTO seat (venue_id, section, row_label, seat_number)
SELECT v.venue_id, s.section, r.row_label, n.seat_number
  FROM venue v
 CROSS JOIN (SELECT 'A' AS section FROM dual UNION ALL SELECT 'B' FROM dual) s
 CROSS JOIN (SELECT TO_CHAR(LEVEL) AS row_label FROM dual CONNECT BY LEVEL <= 5) r
 CROSS JOIN (SELECT LEVEL AS seat_number FROM dual CONNECT BY LEVEL <= 10) n
 WHERE v.name = 'The Chapel';

INSERT INTO show_event (venue_id, title, artist, starts_at)
SELECT venue_id, 'Winter Session', 'The Gloaming', TIMESTAMP '2026-11-14 20:00:00 -08:00' FROM venue WHERE name = 'The Chapel';

INSERT INTO show_event (venue_id, title, artist, starts_at)
SELECT venue_id, 'Late Set', 'Lankum', TIMESTAMP '2026-11-21 21:00:00 -08:00' FROM venue WHERE name = 'The Chapel';

INSERT INTO show_event (venue_id, title, artist, starts_at)
SELECT venue_id, 'Solstice Night', 'Caoimhin O Raghallaigh', TIMESTAMP '2026-12-19 20:30:00 -08:00' FROM venue WHERE name = 'The Chapel';

INSERT INTO show_seat (show_id, seat_id, status, price_cents)
SELECT e.show_id, s.seat_id, 'AVAILABLE',
       CASE s.section WHEN 'A' THEN 8500 ELSE 6000 END
  FROM show_event e
  JOIN seat s ON s.venue_id = e.venue_id;

COMMIT;
