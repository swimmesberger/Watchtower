namespace Watchtower.Application.Persistence;

/// <summary>
/// The PostgreSQL that gives every pre-ADR-0026 <c>stacks</c> / <c>stack_templates</c> row a product:
/// one product per normalized <c>(repository URL, compose file path)</c> across both tables, branch
/// differences becoming <c>branch_override</c>.
/// </summary>
/// <remarks>
/// <para>
/// <b>This is the SQL mirror of <see cref="Services.ProductSourceKey"/>.</b> The normalization has to
/// agree with the C# byte for byte: the migration groups existing rows with this, and
/// <c>stacks.create</c> find-or-creates with that. Two rules that drift produce duplicate products on
/// the first stack created after an upgrade. Both files carry this pointer.
/// </para>
/// <para>
/// A constant rather than a literal inside the migration only so it can be read and reviewed on its
/// own; it belongs to that one migration and is <b>frozen</b>. A future change to the product model
/// gets its own migration and its own statement rather than an edit here — editing this would
/// retroactively change what an already-applied migration did.
/// </para>
/// <para>
/// It runs only over rows whose <c>product_id</c> is still null, so it is idempotent.
/// </para>
/// </remarks>
internal static class ProductBackfillSql {
    /// <summary>
    /// The whole backfill. Runs in the middle of the migration, where <c>products</c> already exists,
    /// <c>stacks</c> / <c>stack_templates</c> already carry nullable <c>product_id</c> and
    /// <c>branch_override</c>, and the four legacy source columns have not been dropped yet.
    /// </summary>
    public const string Sql = """
        -- Step 1: every un-migrated source row with its normalized key.
        --
        -- The URL rule, in the same order as ProductSourceKey.NormalizeRepositoryUrl. btrim is given an
        -- explicit character set because its default trims spaces only, while C#'s string.Trim() takes
        -- every whitespace character — a tab-padded column would otherwise group differently in the two
        -- halves of the rule. The ASCII set below is what can actually reach a git URL column.
        --
        -- In order: trim, drop
        -- trailing slashes, drop a trailing ".git", drop trailing slashes again, then lowercase the
        -- scheme and the host (never the path — git servers are frequently case-sensitive there).
        -- An scp-style "git@host:path" keeps its own key: guessing that it addresses the same
        -- repository as the https form would merge two sources whose credentials differ.
        CREATE TEMP TABLE wt_product_src (
            kind text, row_id integer, nurl text, npath text,
            repository_url text, compose_file_path text, branch text, credential_id integer);

        INSERT INTO wt_product_src (kind, row_id, nurl, npath,
                                    repository_url, compose_file_path, branch, credential_id)
        WITH src AS (
            SELECT 's'::text AS kind, id, repository_url, compose_file_path, branch, credential_id
              FROM stacks WHERE product_id IS NULL
            UNION ALL
            SELECT 't'::text, id, repository_url, compose_file_path, branch, credential_id
              FROM stack_templates WHERE product_id IS NULL
        ),
        stripped AS (
            SELECT s.kind, s.id, s.repository_url, s.compose_file_path, s.branch, s.credential_id,
                   regexp_replace(
                       regexp_replace(
                           regexp_replace(btrim(s.repository_url, E' \t\n\r\f\v'), '/+$', ''),
                           '\.git$', '', 'i'),
                       '/+$', '') AS u,
                   ltrim(btrim(s.compose_file_path, E' \t\n\r\f\v'), '/\') AS npath
              FROM src s
        )
        SELECT st.kind, st.id,
               CASE
                 WHEN scheme.m IS NOT NULL
                   THEN lower(scheme.m[1]) || coalesce(scheme.m[2], '') || lower(scheme.m[3]) || scheme.m[4]
                 WHEN scp.m IS NOT NULL
                   THEN coalesce(scp.m[1], '') || lower(scp.m[2]) || scp.m[3]
                 ELSE st.u
               END,
               st.npath, st.repository_url, st.compose_file_path, st.branch, st.credential_id
          FROM stripped st
          LEFT JOIN LATERAL (
              SELECT regexp_match(st.u, '^([A-Za-z][A-Za-z0-9+.\-]*://)([^/@]*@)?([^/]*)(.*)$') AS m) scheme
            ON true
          LEFT JOIN LATERAL (
              SELECT regexp_match(st.u, '^([^/@:]*@)?([^/:]+)(:.*)$') AS m) scp
            ON true;

        -- Step 2: one product per key. The representative supplying default_branch and credential_id is
        -- the lowest-id stack, or the lowest-id template when no stack uses the key — "min(id)" alone is
        -- ambiguous across two tables with independent id sequences, so the order is spelled out.
        --
        -- The name is the repository's last path segment, disambiguated by the compose file's directory
        -- when several keys share it (acme/web with compose files under apps/api and apps/web becomes
        -- web-apps-api and web-apps-web). A name that is still taken gets a numeric suffix in step 3.
        CREATE TEMP TABLE wt_product_key (
            nurl text, npath text, name text,
            repository_url text, compose_file_path text, branch text, credential_id integer);

        INSERT INTO wt_product_key (nurl, npath, name,
                                    repository_url, compose_file_path, branch, credential_id)
        WITH rep AS (
            SELECT DISTINCT ON (nurl, npath)
                   nurl, npath, repository_url, compose_file_path, branch, credential_id
              FROM wt_product_src
             ORDER BY nurl, npath, kind, row_id
        ),
        named AS (
            SELECT r.*,
                   coalesce(
                       nullif(btrim(regexp_replace(r.nurl, '^.*[/:]', ''), E' \t\n\r\f\v'), ''),
                       'unnamed') AS base_name,
                   nullif(regexp_replace(r.npath, '/[^/]*$', ''), r.npath) AS compose_dir
              FROM rep r
        )
        SELECT n.nurl, n.npath,
               CASE
                 WHEN count(*) OVER (PARTITION BY n.base_name) > 1 AND n.compose_dir IS NOT NULL
                   THEN n.base_name || '-' || replace(n.compose_dir, '/', '-')
                 ELSE n.base_name
               END,
               n.repository_url, n.compose_file_path, n.branch, n.credential_id
          FROM named n;

        -- Step 2b: one product carries one clone credential, so a key whose rows disagree loses all but
        -- the representative's. That is a real decision made on an operator's behalf, and the migration
        -- output is the only place it can be recorded — RAISE NOTICE puts it in the upgrade log next to
        -- the statement that did it. coalesce, because "no credential" and "credential 5" disagree just
        -- as much as two ids do, and count(DISTINCT ...) would not see it.
        DO $wt_credentials$
        DECLARE
            divergent record;
        BEGIN
            FOR divergent IN
                SELECT s.nurl, s.npath,
                       count(DISTINCT coalesce(s.credential_id, -1)) AS variants,
                       max(k.credential_id) AS chosen
                  FROM wt_product_src s
                  JOIN wt_product_key k ON k.nurl = s.nurl AND k.npath = s.npath
                 GROUP BY s.nurl, s.npath
                HAVING count(DISTINCT coalesce(s.credential_id, -1)) > 1
                 ORDER BY s.nurl, s.npath
            LOOP
                RAISE NOTICE
                    'products backfill: % (%) was cloned with % different git credentials; the new product keeps credential %',
                    divergent.nurl, divergent.npath, divergent.variants,
                    coalesce(divergent.chosen::text, 'none');
            END LOOP;
        END
        $wt_credentials$;

        -- Step 3: insert the products, suffixing any name the table (or an earlier row of this batch)
        -- already holds. A loop rather than a row_number() because the suffixed form can collide too,
        -- and products.name is unique — a migration must not be able to fail on a name it chose itself.
        DO $wt_products$
        DECLARE
            candidate text;
            suffix integer;
            row_key record;
        BEGIN
            FOR row_key IN SELECT nurl, npath, name FROM wt_product_key ORDER BY nurl, npath LOOP
                candidate := row_key.name;
                suffix := 1;
                WHILE EXISTS (SELECT 1 FROM products p WHERE p.name = candidate) LOOP
                    suffix := suffix + 1;
                    candidate := row_key.name || '-' || suffix;
                END LOOP;

                INSERT INTO products (name, repository_url, compose_file_path, default_branch,
                                      credential_id, created_at)
                SELECT candidate, k.repository_url, k.compose_file_path, k.branch, k.credential_id, now()
                  FROM wt_product_key k
                 WHERE k.nurl = row_key.nurl AND k.npath = row_key.npath;

                UPDATE wt_product_key SET name = candidate
                 WHERE nurl = row_key.nurl AND npath = row_key.npath;
            END LOOP;
        END
        $wt_products$;

        -- Step 4: point the rows at their product, and keep a branch that differs from the product's
        -- default as an override. Keying products on the branch instead would have forked the catalogue
        -- into per-branch duplicates that then diverge — the denormalization ADR-0026 removes.
        UPDATE stacks s
           SET product_id = p.id,
               branch_override = CASE WHEN s.branch IS DISTINCT FROM p.default_branch THEN s.branch END
          FROM wt_product_src x
          JOIN wt_product_key k ON k.nurl = x.nurl AND k.npath = x.npath
          JOIN products p ON p.name = k.name
         WHERE x.kind = 's' AND x.row_id = s.id;

        UPDATE stack_templates t
           SET product_id = p.id,
               branch_override = CASE WHEN t.branch IS DISTINCT FROM p.default_branch THEN t.branch END
          FROM wt_product_src x
          JOIN wt_product_key k ON k.nurl = x.nurl AND k.npath = x.npath
          JOIN products p ON p.name = k.name
         WHERE x.kind = 't' AND x.row_id = t.id;

        DROP TABLE wt_product_src;
        DROP TABLE wt_product_key;
        """;
}
