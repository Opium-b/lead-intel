
CREATE TABLE public.collector_runs (
    id integer NOT NULL,
    source character varying(64) NOT NULL,
    started_at timestamp with time zone DEFAULT now() NOT NULL,
    finished_at timestamp with time zone,
    status character varying(16) NOT NULL,
    cursor date,
    stats jsonb NOT NULL,
    error text
);

CREATE SEQUENCE public.collector_runs_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.collector_runs_id_seq OWNED BY public.collector_runs.id;

CREATE TABLE public.companies (
    id integer NOT NULL,
    dot_number character varying(16),
    mc_number character varying(16),
    name character varying(255) NOT NULL,
    dba_name character varying(255),
    state character varying(2),
    city character varying(128),
    location character varying(512),
    fleet_size integer,
    drivers integer,
    operating_status character varying(32),
    website character varying(512),
    added_at date,
    attributes jsonb NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    enriched_at timestamp with time zone
);

CREATE SEQUENCE public.companies_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.companies_id_seq OWNED BY public.companies.id;

CREATE TABLE public.contacts (
    id integer NOT NULL,
    company_id integer NOT NULL,
    type character varying(16) NOT NULL,
    value character varying(512) NOT NULL,
    label character varying(128),
    source character varying(64) NOT NULL,
    source_url text,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);

CREATE SEQUENCE public.contacts_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.contacts_id_seq OWNED BY public.contacts.id;

CREATE TABLE public.lead_events (
    id bigint NOT NULL,
    lead_id integer NOT NULL,
    event_type character varying(32) NOT NULL,
    metadata jsonb NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);

CREATE SEQUENCE public.lead_events_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.lead_events_id_seq OWNED BY public.lead_events.id;

CREATE TABLE public.leads (
    id integer NOT NULL,
    company_id integer NOT NULL,
    score integer NOT NULL,
    score_breakdown jsonb NOT NULL,
    status character varying(16) NOT NULL,
    scored_at timestamp with time zone,
    notified_at timestamp with time zone,
    created_at timestamp with time zone DEFAULT now() NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL,
    service_lines jsonb DEFAULT '[]'::jsonb NOT NULL,
    ai_summary jsonb,
    ai_summary_at timestamp with time zone
);

CREATE SEQUENCE public.leads_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.leads_id_seq OWNED BY public.leads.id;

CREATE TABLE public.scoring_rules (
    id integer NOT NULL,
    key character varying(64) NOT NULL,
    label character varying(128) NOT NULL,
    kind character varying(32) NOT NULL,
    weight integer NOT NULL,
    params jsonb NOT NULL,
    enabled boolean NOT NULL,
    updated_at timestamp with time zone DEFAULT now() NOT NULL
);

CREATE SEQUENCE public.scoring_rules_id_seq
    AS integer
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.scoring_rules_id_seq OWNED BY public.scoring_rules.id;

CREATE TABLE public.signals (
    id bigint NOT NULL,
    company_id integer NOT NULL,
    type character varying(64) NOT NULL,
    description text NOT NULL,
    severity character varying(16) NOT NULL,
    source character varying(64) NOT NULL,
    source_url text,
    observed_at date,
    detected_by character varying(64) NOT NULL,
    origin character varying(8) NOT NULL,
    evidence jsonb NOT NULL,
    source_record_id bigint,
    dedupe_key character varying(255) NOT NULL,
    created_at timestamp with time zone DEFAULT now() NOT NULL
);

CREATE SEQUENCE public.signals_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.signals_id_seq OWNED BY public.signals.id;

CREATE TABLE public.source_records (
    id bigint NOT NULL,
    source character varying(64) NOT NULL,
    record_type character varying(64) NOT NULL,
    external_id character varying(128) NOT NULL,
    company_id integer,
    observed_at date,
    source_url text,
    payload jsonb NOT NULL,
    payload_hash character varying(64) NOT NULL,
    fetched_at timestamp with time zone DEFAULT now() NOT NULL
);

CREATE SEQUENCE public.source_records_id_seq
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;

ALTER SEQUENCE public.source_records_id_seq OWNED BY public.source_records.id;

ALTER TABLE ONLY public.collector_runs ALTER COLUMN id SET DEFAULT nextval('public.collector_runs_id_seq'::regclass);

ALTER TABLE ONLY public.companies ALTER COLUMN id SET DEFAULT nextval('public.companies_id_seq'::regclass);

ALTER TABLE ONLY public.contacts ALTER COLUMN id SET DEFAULT nextval('public.contacts_id_seq'::regclass);

ALTER TABLE ONLY public.lead_events ALTER COLUMN id SET DEFAULT nextval('public.lead_events_id_seq'::regclass);

ALTER TABLE ONLY public.leads ALTER COLUMN id SET DEFAULT nextval('public.leads_id_seq'::regclass);

ALTER TABLE ONLY public.scoring_rules ALTER COLUMN id SET DEFAULT nextval('public.scoring_rules_id_seq'::regclass);

ALTER TABLE ONLY public.signals ALTER COLUMN id SET DEFAULT nextval('public.signals_id_seq'::regclass);

ALTER TABLE ONLY public.source_records ALTER COLUMN id SET DEFAULT nextval('public.source_records_id_seq'::regclass);

ALTER TABLE ONLY public.collector_runs
    ADD CONSTRAINT collector_runs_pkey PRIMARY KEY (id);

ALTER TABLE ONLY public.companies
    ADD CONSTRAINT companies_dot_number_key UNIQUE (dot_number);

ALTER TABLE ONLY public.companies
    ADD CONSTRAINT companies_pkey PRIMARY KEY (id);

ALTER TABLE ONLY public.contacts
    ADD CONSTRAINT contacts_company_id_type_value_key UNIQUE (company_id, type, value);

ALTER TABLE ONLY public.contacts
    ADD CONSTRAINT contacts_pkey PRIMARY KEY (id);

ALTER TABLE ONLY public.lead_events
    ADD CONSTRAINT lead_events_pkey PRIMARY KEY (id);

ALTER TABLE ONLY public.leads
    ADD CONSTRAINT leads_company_id_key UNIQUE (company_id);

ALTER TABLE ONLY public.leads
    ADD CONSTRAINT leads_pkey PRIMARY KEY (id);

ALTER TABLE ONLY public.scoring_rules
    ADD CONSTRAINT scoring_rules_key_key UNIQUE (key);

ALTER TABLE ONLY public.scoring_rules
    ADD CONSTRAINT scoring_rules_pkey PRIMARY KEY (id);

ALTER TABLE ONLY public.signals
    ADD CONSTRAINT signals_company_id_dedupe_key_key UNIQUE (company_id, dedupe_key);

ALTER TABLE ONLY public.signals
    ADD CONSTRAINT signals_pkey PRIMARY KEY (id);

ALTER TABLE ONLY public.source_records
    ADD CONSTRAINT source_records_pkey PRIMARY KEY (id);

ALTER TABLE ONLY public.source_records
    ADD CONSTRAINT source_records_source_record_type_external_id_key UNIQUE (source, record_type, external_id);

CREATE INDEX ix_collector_runs_source ON public.collector_runs USING btree (source);

CREATE INDEX ix_companies_mc_number ON public.companies USING btree (mc_number);

CREATE INDEX ix_companies_state ON public.companies USING btree (state);

CREATE INDEX ix_contacts_company_id ON public.contacts USING btree (company_id);

CREATE INDEX ix_lead_events_created_at ON public.lead_events USING btree (created_at);

CREATE INDEX ix_lead_events_lead_id ON public.lead_events USING btree (lead_id);

CREATE INDEX ix_leads_score ON public.leads USING btree (score);

CREATE INDEX ix_leads_status ON public.leads USING btree (status);

CREATE INDEX ix_signals_company_id ON public.signals USING btree (company_id);

CREATE INDEX ix_signals_created_at ON public.signals USING btree (created_at);

CREATE INDEX ix_signals_observed_at ON public.signals USING btree (observed_at);

CREATE INDEX ix_signals_type ON public.signals USING btree (type);

CREATE INDEX ix_source_records_company_id ON public.source_records USING btree (company_id);

ALTER TABLE ONLY public.contacts
    ADD CONSTRAINT contacts_company_id_fkey FOREIGN KEY (company_id) REFERENCES public.companies(id) ON DELETE CASCADE;

ALTER TABLE ONLY public.lead_events
    ADD CONSTRAINT lead_events_lead_id_fkey FOREIGN KEY (lead_id) REFERENCES public.leads(id) ON DELETE CASCADE;

ALTER TABLE ONLY public.leads
    ADD CONSTRAINT leads_company_id_fkey FOREIGN KEY (company_id) REFERENCES public.companies(id) ON DELETE CASCADE;

ALTER TABLE ONLY public.signals
    ADD CONSTRAINT signals_company_id_fkey FOREIGN KEY (company_id) REFERENCES public.companies(id) ON DELETE CASCADE;

ALTER TABLE ONLY public.signals
    ADD CONSTRAINT signals_source_record_id_fkey FOREIGN KEY (source_record_id) REFERENCES public.source_records(id) ON DELETE SET NULL;

ALTER TABLE ONLY public.source_records
    ADD CONSTRAINT source_records_company_id_fkey FOREIGN KEY (company_id) REFERENCES public.companies(id) ON DELETE CASCADE;

