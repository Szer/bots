WITH RECURSIVE
bounds AS (SELECT @start_at::timestamptz AS start_at, @end_at::timestamptz AS end_at),
events AS MATERIALIZED (
 SELECT e.* FROM coupon_event e CROSS JOIN bounds b WHERE e.created_at <= b.end_at
),
reversed_coupons AS (SELECT DISTINCT coupon_id FROM events WHERE event_type LIKE '%_reverted'),
ordered AS (
 SELECT e.*, row_number() OVER (PARTITION BY coupon_id ORDER BY created_at,id) AS rn,
 count(*) OVER (PARTITION BY coupon_id) AS n
 FROM events e JOIN reversed_coupons r USING(coupon_id)
),
replay AS (
 SELECT coupon_id,rn,n,ARRAY[id] AS stack FROM ordered WHERE rn=1
 UNION ALL
 SELECT o.coupon_id,o.rn,o.n,
 CASE WHEN o.event_type LIKE '%_reverted' THEN r.stack[1:cardinality(r.stack)-1]
 ELSE r.stack || o.id END
 FROM replay r JOIN ordered o ON o.coupon_id=r.coupon_id AND o.rn=r.rn+1
),
live AS (
 SELECT e.* FROM events e WHERE NOT EXISTS (SELECT 1 FROM reversed_coupons r WHERE r.coupon_id=e.coupon_id)
 UNION ALL
 SELECT e.* FROM events e JOIN
 (SELECT unnest(stack) AS id FROM replay WHERE rn=n) l USING(id)
),
seq AS (
 SELECT l.*,lead(event_type) OVER w AS next_type,
 lead(created_at) OVER w AS next_at,lead(user_id) OVER w AS next_user
 FROM live l WINDOW w AS (PARTITION BY coupon_id ORDER BY created_at,id)
),
takes AS (
 SELECT s.*,c.expires_at,
 c.expires_at-(s.created_at AT TIME ZONE 'Europe/Dublin')::date AS days_left,
 CASE WHEN next_type IN ('used','returned','reported') AND next_user=user_id
 THEN extract(epoch FROM s.next_at-s.created_at)/3600 END AS hold_hours,
 CASE WHEN next_type='returned' AND next_user=user_id THEN
 c.expires_at-(next_at AT TIME ZONE 'Europe/Dublin')::date END AS return_days_left
 FROM seq s JOIN coupon c ON c.id=s.coupon_id WHERE event_type='taken' AND s.created_at >= (SELECT start_at FROM bounds)
),
adds AS (
 SELECT l.user_id,c.id,c.expires_at-(l.created_at AT TIME ZONE 'Europe/Dublin')::date AS days_left
 FROM live l JOIN coupon c ON c.id=l.coupon_id WHERE event_type='added' AND l.created_at >= (SELECT start_at FROM bounds)
),
ta AS (
 SELECT user_id,count(*) AS takes,round(avg(days_left),2) AS take_days,
 count(hold_hours) AS resolved,round(avg(hold_hours),2) AS hold_hours,
 count(*) FILTER(WHERE next_type='returned' AND next_user=user_id) AS returns,
 round(100.0*count(*) FILTER(WHERE next_type='returned' AND next_user=user_id)/nullif(count(*),0),2) AS return_pct,
 round(avg(hold_hours) FILTER(WHERE next_type='returned'),2) AS return_hold_hours,
 round(avg(return_days_left),2) AS return_days,
 count(*) FILTER(WHERE return_days_left<=1) AS late_returns,
 count(*) FILTER(WHERE return_days_left<=1 AND hold_hours>=24) AS held_day_late_returns,
 count(*) FILTER(WHERE next_type IS NULL) AS unresolved,
 count(*) FILTER(WHERE next_type IS NULL AND expires_at<(SELECT (end_at AT TIME ZONE 'Europe/Dublin')::date FROM bounds)) AS unresolved_expired,
 count(*) FILTER(WHERE next_type='voided') AS void_closed
 FROM takes GROUP BY GROUPING SETS ((user_id),())
),
aa AS (
 SELECT user_id,count(*) AS adds,round(avg(days_left),2) AS add_days,
 count(*) FILTER(WHERE days_left<=1) AS late_adds,
 round(100.0*count(*) FILTER(WHERE days_left<=1)/nullif(count(*),0),2) AS late_add_pct
 FROM adds GROUP BY GROUPING SETS ((user_id),())
),
members AS (
 SELECT u.id,u.username,concat_ws(' ',u.first_name,u.last_name) AS name,
 coalesce(t.takes,0) AS takes,t.take_days,coalesce(t.resolved,0) AS resolved,t.hold_hours,
 coalesce(t.returns,0) AS returns,t.return_pct,t.return_hold_hours,t.return_days,
 coalesce(t.late_returns,0) AS late_returns,coalesce(t.held_day_late_returns,0) AS held_day_late_returns,
 coalesce(t.unresolved,0) AS unresolved,coalesce(t.unresolved_expired,0) AS unresolved_expired,
 coalesce(t.void_closed,0) AS void_closed,coalesce(a.adds,0) AS adds,a.add_days,
 coalesce(a.late_adds,0) AS late_adds,a.late_add_pct
 FROM "user" u LEFT JOIN ta t ON t.user_id=u.id LEFT JOIN aa a ON a.user_id=u.id WHERE u.created_at <= (SELECT end_at FROM bounds)
)
SELECT false AS "Community",id::text AS "Id",coalesce(nullif(name,''),'#'||id::text) AS "Name",username AS "Username",
 takes::integer AS "Takes",
 take_days::double precision AS "TakeDays",
 resolved::integer AS "Resolved",
 hold_hours::double precision AS "HoldHours",
 returns::integer AS "Returns",
 return_pct::double precision AS "ReturnPct",
 return_hold_hours::double precision AS "ReturnHoldHours",
 return_days::double precision AS "ReturnDays",
 late_returns::integer AS "LateReturns",
 held_day_late_returns::integer AS "HeldDayLateReturns",
 unresolved::integer AS "Unresolved",
 unresolved_expired::integer AS "UnresolvedExpired",
 void_closed::integer AS "VoidClosed",
 adds::integer AS "Adds",
 add_days::double precision AS "AddDays",
 late_adds::integer AS "LateAdds",
 late_add_pct::double precision AS "LateAddPct"
FROM members
UNION ALL
SELECT true,'','Сообщество',NULL,
 t.takes::integer,
 t.take_days::double precision,
 t.resolved::integer,
 t.hold_hours::double precision,
 t.returns::integer,
 t.return_pct::double precision,
 t.return_hold_hours::double precision,
 t.return_days::double precision,
 t.late_returns::integer,
 t.held_day_late_returns::integer,
 t.unresolved::integer,
 t.unresolved_expired::integer,
 t.void_closed::integer,
 a.adds::integer,
 a.add_days::double precision,
 a.late_adds::integer,
 a.late_add_pct::double precision
FROM ta t CROSS JOIN aa a WHERE t.user_id IS NULL AND a.user_id IS NULL;
