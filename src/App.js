import express from "express";
import {getConfigVariable} from "./util.js";
import FireflyService from "./FireflyService.js";
import OpenAiService from "./OpenAiService.js";
import MistralService from "./MistralService.js";
import {Server} from "socket.io";
import * as http from "http";
import Queue from "queue";
import JobList from "./JobList.js";
import {MissingEnvironmentVariableException} from "./util.js";

export default class App {
    static #VALID_TRANSACTION_TRIGGERS = ["STORE_TRANSACTION", "TRIGGER_STORE_TRANSACTION", "UPDATE_TRANSACTION", "TRIGGER_UPDATE_TRANSACTION"];
    static #VALID_TRANSACTION_RESPONSES = ["TRANSACTIONS", "RESPONSE_TRANSACTIONS"];
    static #DESTINATION_HISTORY_LIMIT = 5;

    #PORT;
    #ENABLE_UI;

    #firefly;
    //#openAi;
    #mistral;

    #server;
    #io;
    #express;

    #queue;
    #jobList;


    constructor() {
        this.#PORT = getConfigVariable("PORT", '3000');
        this.#ENABLE_UI = getConfigVariable("ENABLE_UI", 'false') === 'true';
    }

    async run() {
        this.#queue = new Queue({
            timeout: 30 * 1000,
            concurrency: 1,
            autostart: true
        });

        this.#queue.addEventListener('start', event => console.log('Job started', event.detail.job))
        this.#queue.addEventListener('success', event => console.log('Job success', event.detail.job))
        this.#queue.addEventListener('error', event => console.error('Job error', event.detail.job, event.detail.error))
        this.#queue.addEventListener('timeout', event => console.log('Job timeout', event.detail.job))

        this.#express = express();
        this.#server = http.createServer(this.#express)
        this.#io = new Server(this.#server)

        this.#jobList = new JobList();
        this.#jobList.on('job created', data => this.#io.emit('job created', data));
        this.#jobList.on('job updated', data => this.#io.emit('job updated', data));

        this.#express.use(express.json());

        if (this.#ENABLE_UI) {
            this.#express.use('/', express.static('public'))
        } else {
            this.#express.get('/', (req, res) => {
                res.status(200).type('text/plain').send('Web UI is disabled. Set ENABLE_UI=true and restart the application to serve the UI at /.');
            });
        }

        this.#express.get('/api/diagnostics', this.#onDiagnostics.bind(this))
        this.#express.post('/api/jobs/:id/fill-missing', this.#onFillMissingJobValue.bind(this))
        this.#express.post('/webhook', this.#onWebhook.bind(this))

        this.#server.listen(this.#PORT, async () => {
            console.log(`Application running on port ${this.#PORT}`);
        });

        this.#io.on('connection', socket => {
            console.log('connected');
            socket.emit('jobs', Array.from(this.#jobList.getJobs().values()));
        })
    }

    #getFireflyService() {
        if (!this.#firefly) {
            this.#firefly = new FireflyService();
        }

        return this.#firefly;
    }

    #getMistralService() {
        if (!this.#mistral) {
            this.#mistral = new MistralService();
        }

        return this.#mistral;
    }

    #getCompletionStatus(data) {
        const hasCategory = Boolean(data?.category);
        const hasBudget = Boolean(data?.budget);

        return hasCategory !== hasBudget ? "partial" : "finished";
    }

    async #onFillMissingJobValue(req, res) {
        const job = this.#jobList.getJob(req.params.id);

        if (!job) {
            res.status(404).json({
                ok: false,
                error: `Job ${req.params.id} was not found.`
            });
            return;
        }

        try {
            const categoryName = job.data?.category ?? null;
            const budgetName = job.data?.budget ?? null;

            if ((!categoryName && !budgetName) || (categoryName && budgetName)) {
                throw new WebhookException("This job is not partially processed.");
            }

            if (!job.data?.transactionId || !Array.isArray(job.data?.transactions) || job.data.transactions.length === 0) {
                throw new WebhookException("This job does not contain enough transaction data to update Firefly III.");
            }

            const firefly = this.#getFireflyService();
            const categories = await firefly.getCategories();
            const budgets = await firefly.getBudgets();
            let categoryId = -1;
            let budgetId = -1;
            let copiedValue = null;
            let updatedField = null;

            if (categoryName && !budgetName) {
                if (!budgets.has(categoryName)) {
                    throw new WebhookException(`No budget exists with the same name as category "${categoryName}".`);
                }

                budgetId = budgets.get(categoryName);
                copiedValue = categoryName;
                updatedField = "budget";
            } else if (budgetName && !categoryName) {
                if (!categories.has(budgetName)) {
                    throw new WebhookException(`No category exists with the same name as budget "${budgetName}".`);
                }

                categoryId = categories.get(budgetName);
                copiedValue = budgetName;
                updatedField = "category";
            }

            await firefly.setCategoryAndBudget(job.data.transactionId, job.data.transactions, categoryId, budgetId);

            const updatedData = {
                ...(job.data ?? {}),
                category: categoryName ?? copiedValue,
                budget: budgetName ?? copiedValue,
                error: null,
                manualUpdate: `Filled missing ${updatedField} with "${copiedValue}".`,
            };

            this.#jobList.updateJobData(job.id, updatedData);
            this.#jobList.setJobFinished(job.id, this.#getCompletionStatus(updatedData));

            res.status(200).json({
                ok: true,
                job: this.#jobList.getJob(job.id)
            });
        } catch (error) {
            console.error(`Manual fill for job ${job.id} failed:`, error);
            this.#jobList.updateJobData(job.id, {
                ...(job.data ?? {}),
                error: error.message,
            });

            res.status(error instanceof WebhookException ? 400 : 500).json({
                ok: false,
                error: error.message,
            });
        }
    }

    async #onDiagnostics(req, res) {
        const diagnostics = await Promise.all([
            this.#runCheck('firefly', async () => await this.#getFireflyService().diagnose()),
            this.#runCheck('mistral', async () => await this.#getMistralService().diagnose())
        ]);

        const ok = diagnostics.every(check => check.ok);
        res.status(ok ? 200 : 503).json({
            ok,
            checks: diagnostics
        });
    }

    async #runCheck(name, fn) {
        try {
            const details = await fn();
            return {
                name,
                ok: true,
                details
            };
        } catch (error) {
            return {
                name,
                ok: false,
                error: error instanceof MissingEnvironmentVariableException
                    ? `Missing environment variable: ${error.variableName}`
                    : error.message
            };
        }
    }

    #onWebhook(req, res) {
        try {
            console.info("Webhook triggered");
            this.#handleWebhook(req, res);
            res.send("Queued");
        } catch (e) {
            console.error(e)
            res.status(400).send(e.message);
        }
    }

    #handleWebhook(req, res) {
        // TODO: validate auth

        if (!App.#VALID_TRANSACTION_TRIGGERS.includes(req.body?.trigger)) {
            throw new WebhookException(`Unsupported webhook trigger "${req.body?.trigger}". Expected one of: ${App.#VALID_TRANSACTION_TRIGGERS.join(", ")}.`);
        }

        if (!App.#VALID_TRANSACTION_RESPONSES.includes(req.body?.response)) {
            throw new WebhookException(`Unsupported webhook response "${req.body?.response}". Expected one of: ${App.#VALID_TRANSACTION_RESPONSES.join(", ")}.`);
        }

        if (!req.body?.content?.id) {
            throw new WebhookException("Missing content.id");
        }

        if (req.body?.content?.transactions?.length === 0) {
            throw new WebhookException("No transactions are available in content.transactions");
        }

        const transaction = req.body.content.transactions[0];

        if (transaction.type !== "withdrawal") {
            throw new WebhookException("content.transactions[0].type has to be 'withdrawal'. Transaction will be ignored.");
        }

        if (transaction.category_name !== null && transaction.budget_name !== null) {
            throw new WebhookException("content.transactions[0].category_name and content.transactions[0].budget_name are already set. Transaction will be ignored.");
        }

        if (!transaction.description) {
            throw new WebhookException("Missing content.transactions[0].description");
        }

        if (!transaction.destination_name) {
            throw new WebhookException("Missing content.transactions[0].destination_name");
        }

        const destinationName = transaction.destination_name;
        const description = transaction.description

        const job = this.#jobList.createJob({
            destinationName,
            description,
            transactionId: req.body.content.id,
            transactions: req.body.content.transactions,
            category: transaction.category_name ?? null,
            budget: transaction.budget_name ?? null,
            error: null,
            manualUpdate: null,
        });

        this.#queue.push(async () => {
            this.#jobList.setJobInProgress(job.id);

            try {
                const firefly = this.#getFireflyService();
                const mistral = this.#getMistralService();
                const categories = await firefly.getCategories();
                const budgets = await firefly.getBudgets();
                let recentTransactions = [];

                try {
                    recentTransactions = await firefly.getRecentTransactionsForDestination(
                        destinationName,
                        App.#DESTINATION_HISTORY_LIMIT,
                        req.body.content.transactions[0].transaction_journal_id ?? null
                    );
                } catch (error) {
                    console.warn(`Could not load recent transactions for destination "${destinationName}": ${error.message}`);
                }

                const allLists = new Map();

                allLists.set('categories', Array.from(categories.keys()));
                allLists.set('budgets', Array.from(budgets.keys()));

                const classification = await mistral.classify(allLists, destinationName, description, recentTransactions);

                const newData = Object.assign({}, job.data);
                newData.category = transaction.category_name ?? classification?.category ?? null;
                newData.budget = transaction.budget_name ?? classification?.budget ?? null;
                newData.prompt = classification?.prompt ?? null;
                newData.response = classification?.response ?? null;
                newData.historyCount = recentTransactions.length;
                newData.error = null;
                newData.manualUpdate = null;

                this.#jobList.updateJobData(job.id, newData);

                const category_id = transaction.category_name === null && categories.has(classification?.category)
                    ? categories.get(classification.category)
                    : -1;
                const budget_id = transaction.budget_name === null && budgets.has(classification?.budget)
                    ? budgets.get(classification.budget)
                    : -1;

                if (category_id !== -1 || budget_id !== -1) {
                    await firefly.setCategoryAndBudget(req.body.content.id, req.body.content.transactions, category_id, budget_id);
                }

                this.#jobList.setJobFinished(job.id, this.#getCompletionStatus(newData));
            } catch (error) {
                console.error(`Job ${job.id} failed:`, error);
                this.#jobList.setJobFailed(job.id, error.message);
            }
        });
    }
}

class WebhookException extends Error {

    constructor(message) {
        super(message);
    }
}
