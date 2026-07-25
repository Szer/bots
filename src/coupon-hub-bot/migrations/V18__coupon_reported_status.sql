-- Add the new 'reported' coupon status (a holder reported the coupon as already used
-- externally). coupon.status has no CHECK constraint or enum, so the value itself needs
-- no schema change.
--
-- The one schema change required: coupon_barcode_active_uniq's partial-unique predicate must
-- include 'reported', or the same barcode could be re-added while a reported (still-unresolved)
-- coupon holds that slot — recreating the duplicate-dead-coupon problem this feature exists to
-- prevent. DbService.fs's TryAddCoupon 23505 race-recovery lookup is updated in lockstep in the
-- same PR (its `status IN (...)` list must match this predicate exactly).
DROP INDEX IF EXISTS coupon_barcode_active_uniq;
CREATE UNIQUE INDEX IF NOT EXISTS coupon_barcode_active_uniq
    ON public.coupon (barcode_text, expires_at)
    WHERE barcode_text IS NOT NULL
      AND status IN ('available', 'taken', 'reported');
